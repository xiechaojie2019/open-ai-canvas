#nullable enable
using System.Globalization;
using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Kernel;

namespace OpenAICanvas.Outbound;

/// <summary>出站自定义请求头。对应 Go: <c>outbound.OutboundHeader</c>。</summary>
public sealed class OutboundHeader
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("value")]
    public string Value { get; set; } = "";
}

/// <summary>
/// 出站 URL/主机 SSRF 校验与自定义请求头编解码。
/// 对应 Go: <c>internal/outbound/outbound.go</c>（本仓库使用的子集）。
/// </summary>
public static class OutboundGuard
{
    private const int MaxOutboundHeaderCount = 32;
    private const int MaxOutboundHeaderBytes = 16 << 10;

    /// <summary>对应 Go: <c>ValidateOutboundURL</c>。</summary>
    public static async Task<Uri> ValidateOutboundUrlAsync(string rawUrl)
    {
        string trimmed = rawUrl.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? parsed) ||
            string.IsNullOrEmpty(parsed.Host))
        {
            throw AppError.BadAuthRequest("外部服务地址无效");
        }
        if (parsed.Scheme is not ("https" or "http"))
        {
            throw AppError.BadAuthRequest("外部服务地址只支持 http/https");
        }
        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            throw AppError.BadAuthRequest("外部服务地址不允许包含认证信息");
        }
        await ValidateOutboundHostAsync(parsed.Host).ConfigureAwait(false);
        return parsed;
    }

    /// <summary>
    /// 校验本地协议地址只解析到 loopback，供 whisper.cpp 等本机服务使用。
    /// </summary>
    public static async Task<Uri> ValidateLoopbackUrlAsync(string rawUrl)
    {
        string trimmed = rawUrl.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? parsed)
            || parsed.Scheme is not ("http" or "https")
            || parsed.Host.Length == 0
            || parsed.UserInfo.Length > 0)
        {
            throw AppError.BadAuthRequest("本地转写服务地址无效");
        }

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(parsed.Host).ConfigureAwait(false);
        }
        catch (Exception error) when (error is SocketException or ArgumentException)
        {
            throw AppError.BadAuthRequest("本地转写服务地址解析失败");
        }
        if (addresses.Length == 0 || addresses.Any(address => !IPAddress.IsLoopback(address)))
        {
            throw AppError.BadAuthRequest("本地转写服务地址必须指向本机");
        }
        return parsed;
    }

    /// <summary>对应 Go: <c>ValidateOutboundHost</c>。</summary>
    public static async Task ValidateOutboundHostAsync(string host)
    {
        await ResolveOutboundHostAsync(host).ConfigureAwait(false);
    }

    /// <summary>
    /// 解析并校验出站主机，返回已通过 SSRF 检查的地址。
    /// 对应 Go: <c>resolveOutboundHost</c>。
    /// </summary>
    public static async Task<IPAddress[]> ResolveOutboundHostAsync(string host)
    {
        string normalized = NormalizeHost(host);
        bool allowPrivate = AllowPrivateUpstreams() || AllowedPrivateUpstreamHost(normalized);
        if (normalized.Length == 0)
        {
            throw AppError.BadAuthRequest("外部服务域名无效");
        }
        if (!allowPrivate && (normalized == "localhost" || normalized.EndsWith(".localhost", StringComparison.Ordinal)))
        {
            throw AppError.BadAuthRequest("不允许访问本机或内网地址");
        }

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(normalized).ConfigureAwait(false);
        }
        catch (System.Net.Sockets.SocketException)
        {
            throw AppError.BadAuthRequest("外部服务域名解析失败");
        }
        catch (ArgumentException)
        {
            throw AppError.BadAuthRequest("外部服务域名解析失败");
        }
        if (addresses.Length == 0)
        {
            throw AppError.BadAuthRequest("外部服务域名没有可用地址");
        }
        if (!allowPrivate)
        {
            foreach (IPAddress ip in addresses)
            {
                if (BlockedOutboundIp(ip))
                {
                    throw AppError.BadAuthRequest("不允许访问本机、内网或链路本地地址");
                }
            }
        }
        return addresses;
    }

    /// <summary>对应 Go: <c>blockedOutboundIP</c>。</summary>
    private static bool BlockedOutboundIp(IPAddress ip) =>
        IPAddress.IsLoopback(ip) || IsPrivate(ip) || IsLinkLocalMulticast(ip) ||
        IsLinkLocalUnicast(ip) || IsUnspecified(ip) || IsMulticast(ip);

    /// <summary>Go <c>net.IP.IsPrivate</c>：RFC1918 与 fc00::/7。</summary>
    private static bool IsPrivate(IPAddress ip)
    {
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            byte[] bytes = ip.GetAddressBytes();
            return bytes[0] == 10 ||
                   (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                   (bytes[0] == 192 && bytes[1] == 168);
        }
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            byte first = ip.GetAddressBytes()[0];
            return (first & 0xFC) == 0xFC;
        }
        return false;
    }

    private static bool IsLinkLocalUnicast(IPAddress ip)
    {
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            byte[] bytes = ip.GetAddressBytes();
            return bytes[0] == 169 && bytes[1] == 254;
        }
        return ip.IsIPv6LinkLocal;
    }

    private static bool IsLinkLocalMulticast(IPAddress ip)
    {
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            byte[] bytes = ip.GetAddressBytes();
            return bytes[0] == 224 && bytes[1] == 0 && bytes[2] == 0;
        }
        byte first = ip.GetAddressBytes()[0];
        return first == 0xFF && (ip.GetAddressBytes()[1] & 0x0F) == 0x01;
    }

    private static bool IsMulticast(IPAddress ip)
    {
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            byte[] bytes = ip.GetAddressBytes();
            return (bytes[0] & 0xF0) == 0xE0;
        }
        return ip.IsIPv6Multicast;
    }

    private static bool IsUnspecified(IPAddress ip) =>
        IPAddress.Equals(ip, IPAddress.Any) || IPAddress.Equals(ip, IPAddress.IPv6Any);

    /// <summary>对应 Go: <c>allowPrivateUpstreams</c>（CANVAS_ALLOW_PRIVATE_UPSTREAMS）。</summary>
    public static bool AllowPrivateUpstreams()
    {
        string value = (Environment.GetEnvironmentVariable("CANVAS_ALLOW_PRIVATE_UPSTREAMS") ?? "")
            .Trim().ToLowerInvariant();
        return value is "1" or "true" or "yes";
    }

    /// <summary>
    /// 显式放行的可信主机（CANVAS_ALLOWED_PRIVATE_UPSTREAM_HOSTS，逗号分隔）。
    /// 对应 Go: <c>AllowedPrivateUpstreamHost</c>。
    /// </summary>
    public static bool AllowedPrivateUpstreamHost(string host)
    {
        host = NormalizeHost(host);
        if (host.Length == 0)
        {
            return false;
        }
        string configured = Environment.GetEnvironmentVariable("CANVAS_ALLOWED_PRIVATE_UPSTREAM_HOSTS") ?? "";
        foreach (string item in configured.Split(','))
        {
            if (NormalizeHost(item) == host)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>对应 Go: <c>normalizeOutboundHost</c>。</summary>
    public static string NormalizeHost(string host) =>
        host.Trim().ToLowerInvariant().TrimEnd('.');

    // ------------------------------------------------------------ 自定义请求头

    /// <summary>对应 Go: <c>EncodeOutboundHeadersJSON</c>。</summary>
    public static string EncodeOutboundHeadersJson(IEnumerable<OutboundHeader> headers)
    {
        List<OutboundHeader> normalized = NormalizeOutboundHeaders(headers);
        return JsonSerializer.Serialize(normalized);
    }

    /// <summary>对应 Go: <c>ParseOutboundHeadersJSON</c>。</summary>
    public static List<OutboundHeader> ParseOutboundHeadersJson(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }
        List<OutboundHeader>? headers;
        try
        {
            headers = JsonSerializer.Deserialize<List<OutboundHeader>>(raw);
        }
        catch (JsonException)
        {
            throw AppError.BadAuthRequest("渠道自定义请求头配置损坏");
        }
        return NormalizeOutboundHeaders(headers ?? []);
    }

    /// <summary>对应 Go: <c>NormalizeOutboundHeaders</c>。</summary>
    public static List<OutboundHeader> NormalizeOutboundHeaders(IEnumerable<OutboundHeader> headers)
    {
        List<OutboundHeader> items = headers.ToList();
        if (items.Count > MaxOutboundHeaderCount)
        {
            throw AppError.BadAuthRequest($"自定义请求头最多支持 {MaxOutboundHeaderCount} 项");
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        List<OutboundHeader> normalized = [];
        int totalBytes = 0;
        foreach (OutboundHeader item in items)
        {
            string name = item.Name.Trim();
            string value = item.Value.Trim();
            if (name.Length == 0 && value.Length == 0)
            {
                continue;
            }
            if (name.Length == 0 || !ValidHeaderName(name))
            {
                throw AppError.BadAuthRequest("自定义请求头名称无效，请使用标准 HTTP Header 名称");
            }
            name = CanonicalHeaderKey(name);
            string lowerName = name.ToLowerInvariant();
            if (BlockedHeader(lowerName))
            {
                throw AppError.BadAuthRequest("请求头 " + name + " 由系统管理，不允许自定义");
            }
            if (!seen.Add(lowerName))
            {
                throw AppError.BadAuthRequest("自定义请求头不能重复：" + name);
            }
            if (!ValidHeaderValue(value))
            {
                throw AppError.BadAuthRequest("请求头 " + name + " 的值包含非法控制字符");
            }
            if (name.Length > 128 || value.Length > 4096)
            {
                throw AppError.BadAuthRequest("单个自定义请求头名称或值过长");
            }
            totalBytes += name.Length + value.Length;
            if (totalBytes > MaxOutboundHeaderBytes)
            {
                throw AppError.BadAuthRequest("自定义请求头总大小不能超过 16KB");
            }
            normalized.Add(new OutboundHeader { Name = name, Value = value });
        }
        return normalized;
    }

    /// <summary>对应 Go: <c>validOutboundHeaderName</c>（token68 字符集）。</summary>
    private static bool ValidHeaderName(string name)
    {
        foreach (char ch in name)
        {
            if (char.IsAsciiLetterOrDigit(ch) || "!#$%&'*+-.^_`|~".Contains(ch))
            {
                continue;
            }
            return false;
        }
        return name.Length > 0;
    }

    /// <summary>对应 Go: <c>validOutboundHeaderValue</c>。</summary>
    private static bool ValidHeaderValue(string value)
    {
        foreach (char ch in value)
        {
            if (ch == '\t' || (ch >= 32 && ch != 127))
            {
                continue;
            }
            return false;
        }
        return true;
    }

    /// <summary>对应 Go: <c>blockedOutboundHeader</c>。</summary>
    private static bool BlockedHeader(string name)
    {
        switch (name)
        {
            case "authorization":
            case "proxy-authorization":
            case "cookie":
            case "set-cookie":
            case "host":
            case "content-length":
            case "content-type":
            case "accept":
            case "connection":
            case "proxy-connection":
            case "keep-alive":
            case "transfer-encoding":
            case "te":
            case "trailer":
            case "upgrade":
            case "forwarded":
            case "x-goog-api-key":
                return true;
        }
        return name.StartsWith("x-canvas-", StringComparison.Ordinal) ||
               name.StartsWith("x-forwarded-", StringComparison.Ordinal);
    }

    /// <summary>Go <c>http.CanonicalHeaderKey</c>：连字符后首字母大写，其余小写。</summary>
    private static string CanonicalHeaderKey(string name)
    {
        StringBuilder builder = new(name.Length);
        bool upper = true;
        foreach (char ch in name)
        {
            builder.Append(upper ? char.ToUpper(ch, CultureInfo.InvariantCulture) : char.ToLower(ch, CultureInfo.InvariantCulture));
            upper = ch == '-';
        }
        return builder.ToString();
    }
}
