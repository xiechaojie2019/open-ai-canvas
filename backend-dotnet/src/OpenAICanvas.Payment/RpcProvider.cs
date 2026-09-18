#nullable enable
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenAICanvas.Payment;

/// <summary>
/// 支付插件进程宿主（<c>yingce.payment/v1</c> 协议）。
/// 对应 Go: <c>internal/payment/rpc.go: RPCProvider</c>。
/// </summary>
/// <remarks>
/// <para>
/// <b>协议形态</b>：不是 gRPC、也不是 Go 的 <c>net/rpc</c>，而是最朴素的
/// <b>stdio 单行 JSON</b>：宿主启动插件进程 → stdin 写一行 JSON 请求 →
/// stdout 读一行 JSON 响应 → 进程退出。每次调用起一个新进程，无长连接。
/// </para>
/// <para>
/// 因此 .NET 8 <b>不需要任何第三方 RPC 库</b>，标准库的
/// <see cref="System.Diagnostics.Process"/> 即可完整实现。
/// </para>
/// <para>
/// 安全约束（与 Go 一致）：插件包目录为工作目录；环境变量清空到只剩
/// <c>PATH</c>/<c>HOME</c>；输出上限 2MB；执行超时 30 秒；
/// Linux 下校验 ELF 架构与宿主一致，防止把 Windows PE 或异架构二进制当插件跑。
/// </para>
/// </remarks>
public sealed class RpcPaymentProvider : IPaymentProvider
{
    /// <summary>协议版本。对应 Go: <c>pluginRPCVersion</c>。</summary>
    public const string ProtocolVersion = "yingce.payment/v1";

    /// <summary>响应上限 2MB。对应 Go: <c>pluginRPCMaxOutput</c>。</summary>
    private const int MaxOutputBytes = 2 << 20;

    /// <summary>stderr 读取上限 64KB（仅用于排空管道，不参与判定）。</summary>
    private const int MaxStderrBytes = 64 << 10;

    /// <summary>执行超时 30 秒。对应 Go: <c>pluginRPCTimeout</c>。</summary>
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(30);

    private readonly string _command;
    private readonly string _workingDirectory;

    /// <summary>
    /// 构造宿主。
    /// </summary>
    /// <param name="descriptor">插件描述符。</param>
    /// <param name="packageDir">插件包根目录（作为进程工作目录）。</param>
    /// <param name="entry">
    /// 相对 <paramref name="packageDir"/> 的可执行文件路径，必须以 <c>backend/</c> 开头。
    /// 该前缀限制是 Go 侧既有的约定，用于把插件可执行文件约束在包内的 backend 目录。
    /// </param>
    public RpcPaymentProvider(PaymentProviderDescriptor descriptor, string packageDir, string entry)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (string.IsNullOrWhiteSpace(descriptor.ID) || string.IsNullOrWhiteSpace(descriptor.PluginID))
        {
            throw new ArgumentException("payment plugin descriptor requires id and plugin id", nameof(descriptor));
        }

        string normalized = entry.Replace('\\', '/');
        if (Path.IsPathRooted(entry)
            || !string.Equals(NormalizeRelative(normalized), normalized, StringComparison.Ordinal)
            || !normalized.StartsWith("backend/", StringComparison.Ordinal))
        {
            throw new ArgumentException("payment plugin entry must be relative to backend/", nameof(entry));
        }

        if (string.IsNullOrWhiteSpace(packageDir))
        {
            throw new ArgumentException("payment plugin package directory is empty", nameof(packageDir));
        }

        Descriptor = descriptor;
        _workingDirectory = packageDir;
        _command = Path.Combine(packageDir, entry.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <inheritdoc />
    public PaymentProviderDescriptor Descriptor { get; }

    // ---------------------------------------------------------------- 操作

    /// <inheritdoc />
    public Task ValidateConfigAsync(
        Dictionary<string, string> config, CancellationToken cancellationToken = default) =>
        CallAsync(new RpcRequest
        {
            Version = ProtocolVersion,
            Operation = "validate_config",
            Config = config,
        }, output: null, cancellationToken);

    /// <inheritdoc />
    public Task<Checkout> CreateOrderAsync(
        Dictionary<string, string> config, CreateRequest request, CancellationToken cancellationToken = default) =>
        CallAsync<Checkout>(new RpcRequest
        {
            Version = ProtocolVersion,
            Operation = "create_order",
            Config = config,
            Request = JsonSerializer.SerializeToElement(request, PaymentJson.Options),
        }, cancellationToken);

    /// <inheritdoc />
    public Task<PaymentResult> QueryOrderAsync(
        Dictionary<string, string> config, QueryRequest request, CancellationToken cancellationToken = default) =>
        CallAsync<PaymentResult>(new RpcRequest
        {
            Version = ProtocolVersion,
            Operation = "query_order",
            Config = config,
            Request = JsonSerializer.SerializeToElement(request, PaymentJson.Options),
        }, cancellationToken);

    /// <inheritdoc />
    public Task<PaymentResult> CloseOrderAsync(
        Dictionary<string, string> config, CloseRequest request, CancellationToken cancellationToken = default) =>
        CallAsync<PaymentResult>(new RpcRequest
        {
            Version = ProtocolVersion,
            Operation = "close_order",
            Config = config,
            Request = JsonSerializer.SerializeToElement(request, PaymentJson.Options),
        }, cancellationToken);

    /// <inheritdoc />
    public Task<PaymentNotification> VerifyNotificationAsync(
        Dictionary<string, string> config,
        IReadOnlyDictionary<string, string[]> headers,
        byte[] rawBody,
        CancellationToken cancellationToken = default) =>
        CallAsync<PaymentNotification>(new RpcRequest
        {
            Version = ProtocolVersion,
            Operation = "verify_notification",
            Config = config,
            Headers = headers.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            Body = Convert.ToBase64String(rawBody),
        }, cancellationToken);

    /// <inheritdoc />
    public Task<List<BillRecord>> DownloadTradeBillAsync(
        Dictionary<string, string> config, DateTime billDate, CancellationToken cancellationToken = default) =>
        CallAsync<List<BillRecord>>(new RpcRequest
        {
            Version = ProtocolVersion,
            Operation = "download_trade_bill",
            Config = config,
            // Go 用 "2006-01-02"，即本地日期部分。
            BillDate = billDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        }, cancellationToken);

    // ---------------------------------------------------------------- 调用

    private async Task CallAsync(RpcRequest request, object? output, CancellationToken cancellationToken)
    {
        await CallAsync<object>(request, cancellationToken, output).ConfigureAwait(false);
    }

    private async Task<T> CallAsync<T>(RpcRequest request, CancellationToken cancellationToken)
        => await CallAsync<T>(request, cancellationToken, output: null).ConfigureAwait(false) ?? default!;

    private async Task<T?> CallAsync<T>(
        RpcRequest request, CancellationToken cancellationToken, object? output)
    {
        if (string.IsNullOrWhiteSpace(_command))
        {
            throw new InvalidOperationException("payment plugin runtime is unavailable");
        }

        // ---- 启动前的可执行文件检查（对应 Go 的 os.Stat + 权限位 + 架构校验） ----
        FileInfo info = new(_command);
        if (info.Exists && info.Attributes.HasFlag(FileAttributes.Directory))
        {
            throw ClassifyStartError(new UnauthorizedAccessException(
                $"payment plugin executable is a directory: {_command}"));
        }

        if (!info.Exists)
        {
            throw ClassifyStartError(new FileNotFoundException("payment plugin executable not found", _command));
        }

        if (!OperatingSystem.IsWindows())
        {
            // Go 检查 0o111 执行位；.NET 无直接等价 API，用 UnixFileMode。
            UnixFileMode mode = File.GetUnixFileMode(_command);
            const UnixFileMode executeBits =
                UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            if ((mode & executeBits) == 0)
            {
                throw ClassifyStartError(new UnauthorizedAccessException(
                    $"payment plugin executable has no execute bit: {_command}"));
            }
        }

        ValidateExecutablePlatform(_command);

        string payload = JsonSerializer.Serialize(request, PaymentJson.Options);

        ProcessStartInfo startInfo = new()
        {
            FileName = _command,
            WorkingDirectory = _workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // 环境变量清空到只剩最小集合，避免插件读到宿主的密钥类变量。
        startInfo.EnvironmentVariables.Clear();
        startInfo.EnvironmentVariables["PATH"] = "/usr/bin:/bin";
        startInfo.EnvironmentVariables["HOME"] = "/nonexistent";

        using Process process = new() { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception error)
        {
            throw ClassifyStartError(error);
        }

        // 关键：写完请求必须关闭 stdin。
        // 插件侧是「读到 EOF 才算拿到完整请求」（Go 的 cmd.Stdin = strings.NewReader(...)
        // 也是这个语义），不关管道插件会一直阻塞在读取上直到超时。
        try
        {
            await process.StandardInput.WriteAsync(payload.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            await process.StandardInput.WriteAsync("\n".AsMemory(), cancellationToken).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            // 插件可能在读取前就退出；后续按退出码/输出判定，这里不抢先报错。
        }
        finally
        {
            process.StandardInput.Close();
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CallTimeout);

        // 并发读 stdout/stderr，避免插件写满 stderr 管道后互相阻塞。
        Task<byte[]> stdoutTask = ReadCappedAsync(process.StandardOutput.BaseStream, MaxOutputBytes, timeout.Token);
        Task<byte[]> stderrTask = ReadCappedAsync(process.StandardError.BaseStream, MaxStderrBytes, timeout.Token);

        // 先读完 stdout 再等退出。
        // 判定顺序必须与 Go 一致：Go 用 io.ReadAll(LimitReader(stdout, max+1)) 读满即返回，
        // 之后才检查「长度超限」，最后才看 ctx 是否超时。若反过来先等进程退出，
        // 插件写满管道会被阻塞、进程不退出，于是错误地报成 plugin_timeout。
        byte[] data;
        try
        {
            data = await stdoutTask.ConfigureAwait(false);
        }
        catch (OutputTooLargeException error)
        {
            KillQuietly(process);
            throw new InvalidOperationException("支付插件响应超过安全限制", error);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            KillQuietly(process);
            throw new PaymentProviderException("plugin_timeout", "支付插件执行超时", temporary: true);
        }
        catch (Exception error)
        {
            KillQuietly(process);
            throw new PaymentProviderException("plugin_read_failed", "读取支付插件响应失败", temporary: true, error);
        }

        // stderr 仅用于排空管道，读失败不影响结果（与 Go 的 `_ = io.ReadAll(stderr)` 一致）。
        try
        {
            _ = await stderrTask.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 忽略。
        }

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            KillQuietly(process);
            throw new PaymentProviderException("plugin_timeout", "支付插件执行超时", temporary: true);
        }
        catch (OperationCanceledException)
        {
            KillQuietly(process);
            throw;
        }

        if (process.ExitCode != 0)
        {
            throw new PaymentProviderException(
                "plugin_process_failed", "支付插件进程异常退出", temporary: true,
                new InvalidOperationException($"exit code {process.ExitCode}"));
        }

        RpcResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<RpcResponse>(data, PaymentJson.Options);
        }
        catch (JsonException error)
        {
            throw new PaymentProviderException(
                "plugin_invalid_response", "支付插件返回了无效响应", temporary: true, error);
        }

        if (response is null)
        {
            throw new PaymentProviderException("plugin_invalid_response", "支付插件返回了无效响应", temporary: true);
        }

        if (!response.OK)
        {
            string message = response.Message;
            if (message.Length == 0)
            {
                message = response.Error;
            }
            if (message.Length == 0)
            {
                message = "支付插件返回失败";
            }
            // 上游「订单不存在」需要被上层识别成可继续流程的信号（而非硬失败），
            // 与 Go 的 errors.Is(err, ErrOrderNotFound) 语义对齐。
            if (PaymentErrors.IsOrderNotFound(message))
            {
                throw new PaymentOrderNotFoundException(message);
            }

            if (PaymentErrors.IsTradeBillNotFound(message))
            {
                throw new PaymentTradeBillNotFoundException(message);
            }

            throw new PaymentProviderException(response.Code, message);
        }

        if (output is null && typeof(T) == typeof(object))
        {
            return default;
        }

        if (response.Data is null || response.Data.Value.ValueKind == JsonValueKind.Null)
        {
            return default;
        }

        try
        {
            return response.Data.Value.Deserialize<T>(PaymentJson.Options);
        }
        catch (JsonException error)
        {
            throw new InvalidOperationException("解析支付插件数据失败", error);
        }
    }

    /// <summary>
    /// 读取上限内的全部字节；超过上限抛 <see cref="OutputTooLargeException"/>。
    /// 对应 Go 的 <c>io.ReadAll(io.LimitReader(stream, max+1))</c> + 长度检查。
    /// </summary>
    private static async Task<byte[]> ReadCappedAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        using MemoryStream collected = new();
        byte[] buffer = new byte[64 * 1024];

        while (true)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            collected.Write(buffer, 0, read);
            if (collected.Length > maxBytes)
            {
                throw new OutputTooLargeException();
            }
        }

        return collected.ToArray();
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // 进程可能已退出或无权 kill；忽略，由上层错误分类处理。
        }
    }

    /// <summary>
    /// 把启动期异常分类成稳定错误码。对应 Go: <c>classifyPaymentPluginStartError</c>。
    /// </summary>
    private static PaymentProviderException ClassifyStartError(Exception cause)
    {
        PaymentProviderException error = cause switch
        {
            FileNotFoundException or DirectoryNotFoundException =>
                new PaymentProviderException("plugin_executable_missing", "支付插件可执行文件或其运行时加载器不可用", cause: cause),
            UnauthorizedAccessException =>
                new PaymentProviderException("plugin_permission_denied", "支付插件没有执行权限", cause: cause),
            ExecFormatException =>
                new PaymentProviderException("plugin_exec_format_error", "支付插件可执行文件与当前操作系统或 CPU 架构不兼容", cause: cause),
            _ => new PaymentProviderException("plugin_start_failed", "支付插件启动失败", temporary: true, cause: cause),
        };

        Console.Error.WriteLine(
            $"payment plugin executable failed: command={cause.GetType().Name} code={error.Code} error={cause.Message}");
        return error;
    }

    // ---------------------------------------------------------------- 平台校验

    /// <summary>
    /// Linux 下校验可执行文件架构与宿主一致。
    /// 对应 Go: <c>validatePaymentExecutablePlatform</c>。
    /// </summary>
    /// <remarks>
    /// 把 Windows PE / Mach-O / 异架构 ELF 提前拦下，避免落到 <c>execve</c> 才报
    /// ENOEXEC（错误信息更含糊）。脚本等未知格式交给进程启动本身判定。
    /// </remarks>
    private static void ValidateExecutablePlatform(string command)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using FileStream stream = File.OpenRead(command);
        Span<byte> header = stackalloc byte[20];
        int read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        if (read < 4)
        {
            throw new ExecFormatException("payment plugin executable header is incomplete");
        }

        // Windows PE
        if (header[0] == (byte)'M' && header[1] == (byte)'Z')
        {
            throw new ExecFormatException("payment plugin executable is Windows PE on linux");
        }

        // Mach-O（四种字节序/位宽组合）
        if (IsMachOMagic(header))
        {
            throw new ExecFormatException("payment plugin executable is Mach-O on linux");
        }

        // ELF：校验位宽与机器码
        if (!(header[0] == 0x7F && header[1] == (byte)'E' && header[2] == (byte)'L' && header[3] == (byte)'F'))
        {
            // 未知格式交给进程启动判定。
            return;
        }

        if (read < 20)
        {
            throw new ExecFormatException("payment plugin ELF header is incomplete");
        }

        byte elfClass = header[4];
        byte elfData = header[5];
        if (elfClass != 2)
        {
            throw new ExecFormatException("payment plugin ELF is not 64-bit");
        }

        ushort machine = elfData == 2
            ? BinaryPrimitives.ReadUInt16BigEndian(header[18..])
            : BinaryPrimitives.ReadUInt16LittleEndian(header[18..]);

        // EM_X86_64 = 62，EM_AARCH64 = 183
        ushort? expected = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => 62,
            Architecture.Arm64 => 183,
            _ => null,
        };

        if (expected.HasValue && machine != expected.Value)
        {
            throw new ExecFormatException(
                $"payment plugin ELF architecture mismatch: host={RuntimeInformation.ProcessArchitecture} machine={machine}");
        }
    }

    private static bool IsMachOMagic(ReadOnlySpan<byte> header) =>
        (header[0], header[1], header[2], header[3]) switch
        {
            (0xFE, 0xED, 0xFA, 0xCE) => true,
            (0xCE, 0xFA, 0xED, 0xFE) => true,
            (0xFE, 0xED, 0xFA, 0xCF) => true,
            (0xCF, 0xFA, 0xED, 0xFE) => true,
            _ => false,
        };

    private static string NormalizeRelative(string path)
    {
        string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        List<string> stack = [];
        foreach (string part in parts)
        {
            if (part == ".")
            {
                continue;
            }
            if (part == "..")
            {
                if (stack.Count == 0)
                {
                    return path; // 无法归一化，交由调用方判定为非法
                }
                stack.RemoveAt(stack.Count - 1);
                continue;
            }
            stack.Add(part);
        }
        return string.Join("/", stack);
    }

    /// <summary>可执行文件格式与当前平台不兼容。对应 Go 的 <c>syscall.ENOEXEC</c>。</summary>
    private sealed class ExecFormatException : Exception
    {
        public ExecFormatException(string message) : base(message)
        {
        }
    }

    /// <summary>插件输出超过安全上限。</summary>
    private sealed class OutputTooLargeException : Exception
    {
        public OutputTooLargeException() : base("plugin output exceeds limit")
        {
        }
    }

    // ---------------------------------------------------------------- 协议结构

    /// <summary>RPC 请求。对应 Go: <c>internal/payment.rpcRequest</c>。</summary>
    private sealed class RpcRequest
    {
        [JsonPropertyName("version")]
        public string Version { get; set; } = "";

        [JsonPropertyName("operation")]
        public string Operation { get; set; } = "";

        [JsonPropertyName("config")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, string>? Config { get; set; }

        [JsonPropertyName("request")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public JsonElement? Request { get; set; }

        [JsonPropertyName("headers")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, string[]>? Headers { get; set; }

        [JsonPropertyName("bodyBase64")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Body { get; set; }

        [JsonPropertyName("billDate")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? BillDate { get; set; }
    }

    /// <summary>RPC 响应。对应 Go: <c>internal/payment.rpcResponse</c>。</summary>
    private sealed class RpcResponse
    {
        [JsonPropertyName("ok")]
        public bool OK { get; set; }

        [JsonPropertyName("error")]
        public string Error { get; set; } = "";

        [JsonPropertyName("code")]
        public string Code { get; set; } = "";

        [JsonPropertyName("message")]
        public string Message { get; set; } = "";

        [JsonPropertyName("data")]
        public JsonElement? Data { get; set; }
    }
}
