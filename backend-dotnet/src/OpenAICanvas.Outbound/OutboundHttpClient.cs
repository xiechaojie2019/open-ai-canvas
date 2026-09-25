#nullable enable
using System.Net;
using System.Net.Sockets;

namespace OpenAICanvas.Outbound;

/// <summary>
/// 受控出站 HttpClient：连接时解析目标主机并复用 SSRF 校验策略，再拨号到已验证的 IP，
/// TLS SNI 与 Host 头仍使用原始主机名。对应 Go: <c>outbound.newOutboundTransport</c>。
/// </summary>
/// <remarks>
/// 代理地址由部署者通过 HTTP(S)_PROXY 环境变量配置（.NET DefaultProxy 同源读取）；
/// 代理主机本身不做 SSRF 解析（与 Go 的 <c>configuredProxyHost</c> 旁路一致），
/// 目标 URL 仍在请求前经过 <see cref="OutboundGuard.ValidateOutboundUrlAsync"/> 校验。
/// </remarks>
public static class OutboundHttpClient
{
    /// <summary>连接超时。对应 Go: <c>net.Dialer{Timeout: 15s}</c>。</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    /// <summary>创建受控出站客户端。调用方负责释放。</summary>
    /// <param name="useProxy">是否使用进程配置的默认代理。</param>
    /// <param name="allowLoopbackOnly">仅允许解析到 loopback 地址；供本机专用协议调用方使用。</param>
    public static HttpClient Create(
        TimeSpan? timeout = null,
        bool allowAutoRedirect = true,
        bool useProxy = true,
        bool allowLoopbackOnly = false)
    {
        SocketsHttpHandler handler = new()
        {
            Proxy = useProxy ? HttpClient.DefaultProxy : null,
            AllowAutoRedirect = allowAutoRedirect,
            ConnectCallback = (context, cancellationToken) =>
                ConnectThroughSsrfGuardAsync(context, allowLoopbackOnly, useProxy, cancellationToken),
            ConnectTimeout = ConnectTimeout,
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            MaxConnectionsPerServer = 20,
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(90),
            SslOptions = { EnabledSslProtocols = System.Security.Authentication.SslProtocols.None },
        };

        HttpClient client = new(handler)
        {
            Timeout = timeout ?? TimeSpan.FromMinutes(5),
        };
        return client;
    }

    private static async ValueTask<Stream> ConnectThroughSsrfGuardAsync(
        SocketsHttpConnectionContext context,
        bool allowLoopbackOnly,
        bool useProxy,
        CancellationToken cancellationToken)
    {
        string host = context.DnsEndPoint.Host;
        int port = context.DnsEndPoint.Port;

        // 代理主机由部署者配置，跳过 SSRF 解析直连。
        if (useProxy && !allowLoopbackOnly && ConfiguredProxyHost(host))
        {
            // 代理地址不做 SSRF 解析，直接按主机名连接（由系统解析）。
            Socket socket = new(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        // Eagle 等本机协议先在应用层严格校验 URL，再由这里确认 DNS 结果仍全部是回环地址。
        IPAddress[] addresses = allowLoopbackOnly
            ? await ResolveLoopbackHostAsync(host).ConfigureAwait(false)
            // 解析与 SSRF 校验一次完成；按序尝试全部地址直到连通（与 Go net.Dialer 行为一致）。
            : await OutboundGuard.ResolveOutboundHostAsync(host).ConfigureAwait(false);
        return await ConnectAsync(addresses, port, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IPAddress[]> ResolveLoopbackHostAsync(string host)
    {
        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host).ConfigureAwait(false);
        }
        catch (SocketException error)
        {
            throw new HttpRequestException("loopback host resolution failed", error);
        }

        if (addresses.Length == 0 || addresses.Any(address => !IPAddress.IsLoopback(address)))
        {
            throw new HttpRequestException("loopback-only host resolved to a non-loopback address");
        }

        return addresses;
    }

    private static async Task<Stream> ConnectAsync(IPAddress[] addresses, int port, CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        foreach (IPAddress address in addresses)
        {
            Socket socket = new(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true,
            };
            try
            {
                await socket.ConnectAsync(address, port, cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception error) when (error is SocketException)
            {
                socket.Dispose();
                lastError = error;
            }
        }
        throw lastError ?? new SocketException((int)SocketError.NotConnected);
    }

    /// <summary>对应 Go: <c>configuredProxyHost</c>。目标主机是环境变量配置的代理时返回 true。</summary>
    public static bool ConfiguredProxyHost(string host)
    {
        host = OutboundGuard.NormalizeHost(host);
        foreach (string name in new[] { "HTTPS_PROXY", "https_proxy", "HTTP_PROXY", "http_proxy" })
        {
            string raw = (Environment.GetEnvironmentVariable(name) ?? "").Trim();
            if (raw.Length == 0)
            {
                continue;
            }
            if (!raw.Contains("://"))
            {
                raw = "http://" + raw;
            }
            if (Uri.TryCreate(raw, UriKind.Absolute, out Uri? parsed) &&
                OutboundGuard.NormalizeHost(parsed.Host) == host)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>把渠道自定义头附加到请求。对应 Go: <c>ApplyOutboundHeaders</c>。</summary>
    public static void ApplyHeaders(HttpRequestMessage request, IEnumerable<OutboundHeader> headers)
    {
        foreach (OutboundHeader header in headers)
        {
            if (!request.Headers.TryAddWithoutValidation(header.Name, header.Value))
            {
                request.Content?.Headers.TryAddWithoutValidation(header.Name, header.Value);
            }
        }
    }

    /// <summary>
    /// 解析 <c>Retry-After</c>：优先按秒数，其次按 HTTP 日期（仅当晚于 <paramref name="now"/>）。
    /// 无法解析或已过期一律返回 <see cref="TimeSpan.Zero"/>。
    /// 对应 Go: <c>parseRetryAfter</c>。
    /// </summary>
    public static TimeSpan ParseRetryAfter(string? value, DateTimeOffset now)
    {
        string raw = (value ?? "").Trim();
        if (raw.Length == 0)
        {
            return TimeSpan.Zero;
        }
        // Go 用 strconv.Atoi：只接受可选符号 + 数字，不接受小数或前后缀。
        if (IsInteger(raw, out long seconds))
        {
            return seconds > 0 ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero;
        }
        if (DateTimeOffset.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset at))
        {
            if (at > now)
            {
                return at - now;
            }
        }
        return TimeSpan.Zero;
    }

    private static bool IsInteger(string value, out long result)
    {
        result = 0;
        if (value.Length == 0)
        {
            return false;
        }
        int index = 0;
        bool negative = false;
        if (value[0] is '+' or '-')
        {
            negative = value[0] == '-';
            index = 1;
            if (value.Length == 1)
            {
                return false;
            }
        }
        long accumulated = 0;
        for (int position = index; position < value.Length; position++)
        {
            char ch = value[position];
            if (ch is < '0' or > '9')
            {
                return false;
            }
            checked
            {
                try
                {
                    accumulated = (accumulated * 10) + (ch - '0');
                }
                catch (OverflowException)
                {
                    return false;
                }
            }
        }
        result = negative ? -accumulated : accumulated;
        return true;
    }
}
