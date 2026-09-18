using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace OpenAICanvas.Web.Startup;

/// <summary>
/// 运行配置。环境变量名与 Go 版<b>逐字一致</b>，默认值也一致。
/// </summary>
/// <remarks>对应 Go: cmd/server/main.go 的 <c>env</c> / <c>envBool</c> / <c>envDuration</c>。</remarks>
public sealed class CanvasEnvironment
{
    /// <summary>对应 <c>CANVAS_BACKEND_DATA_DIR</c>，默认 <c>data</c>。</summary>
    public string DataDir { get; private init; } = "data";

    /// <summary>对应 <c>CANVAS_DATABASE_DRIVER</c>，默认 <c>sqlite</c>。</summary>
    public string DatabaseDriver { get; private init; } = "sqlite";

    /// <summary>对应 <c>DATABASE_URL</c>。</summary>
    public string? DatabaseUrl { get; private init; }

    /// <summary>对应 <c>CANVAS_AUTO_MIGRATE</c>，默认 true。</summary>
    public bool AutoMigrate { get; private init; } = true;

    /// <summary>对应 <c>CANVAS_BACKEND_ADDR</c>，默认 <c>:8080</c>。</summary>
    public string Address { get; private init; } = ":8080";

    /// <summary>对应 <c>CANVAS_UPDATER_TOKEN</c>。</summary>
    public string? UpdaterToken { get; private init; }

    /// <summary>对应 <c>CANVAS_UPDATER_SOCKET</c>。</summary>
    public string UpdaterSocket { get; private init; } = "/run/open-ai-canvas-updater/updater.sock";

    /// <summary>对应 <c>CANVAS_CORS_ORIGINS</c>。</summary>
    public string? CorsOrigins { get; private init; }

    /// <summary>对应 <c>CANVAS_SHUTDOWN_TIMEOUT</c>，默认 10m。</summary>
    public TimeSpan ShutdownTimeout { get; private init; } = TimeSpan.FromMinutes(10);

    /// <summary>对应 <c>CANVAS_ALLOWED_PRIVATE_UPSTREAM_HOSTS</c>。</summary>
    public string? AllowedPrivateUpstreamHosts { get; private init; }

    /// <summary>对应 <c>CANVAS_ALLOW_PRIVATE_UPSTREAMS</c>。</summary>
    public bool AllowPrivateUpstreams { get; private init; }

    /// <summary>对应 <c>CANVAS_WORKER_CONCURRENCY</c>，默认 3。</summary>
    public int WorkerConcurrency { get; private init; } = 3;

    /// <summary>对应 <c>CANVAS_CHANNEL_CONCURRENCY</c>，默认 3。</summary>
    public int ChannelConcurrency { get; private init; } = 3;

    /// <summary>对应 <c>CANVAS_WHISPER_BASE_URL</c>。</summary>
    public string? WhisperBaseUrl { get; private init; }

    /// <summary>对应 <c>CANVAS_FFMPEG_PATH</c>。</summary>
    public string? FfmpegPath { get; private init; }

    /// <summary>对应 <c>REDIS_URL</c>。</summary>
    public string? RedisUrl { get; private init; }

    /// <summary>Kestrel 监听地址（由 <see cref="Address"/> 转换而来）。</summary>
    public string ListenUrl { get; private init; } = "http://0.0.0.0:8080";

    /// <summary>
    /// 从宿主配置读取。ASP.NET Core 的配置源默认已包含环境变量，
    /// 因此这里读到的值与 Go 直接读 <c>os.Getenv</c> 一致；
    /// 额外的好处是支持 <c>WebApplicationFactory.UseSetting</c> 等宿主级覆盖（集成测试需要）。
    /// </summary>
    public static CanvasEnvironment FromConfiguration(IConfiguration configuration)
    {
        string address = Read(configuration, "CANVAS_BACKEND_ADDR") ?? ":8080";
        return new CanvasEnvironment
        {
            DataDir = Read(configuration, "CANVAS_BACKEND_DATA_DIR") ?? "data",
            DatabaseDriver = Read(configuration, "CANVAS_DATABASE_DRIVER") ?? "sqlite",
            DatabaseUrl = Read(configuration, "DATABASE_URL"),
            AutoMigrate = ReadBool(configuration, "CANVAS_AUTO_MIGRATE", true),
            Address = address,
            ListenUrl = ToListenUrl(address),
            UpdaterToken = Read(configuration, "CANVAS_UPDATER_TOKEN"),
            UpdaterSocket = Read(configuration, "CANVAS_UPDATER_SOCKET") ?? "/run/open-ai-canvas-updater/updater.sock",
            CorsOrigins = Read(configuration, "CANVAS_CORS_ORIGINS"),
            ShutdownTimeout = ReadDuration(configuration, "CANVAS_SHUTDOWN_TIMEOUT", TimeSpan.FromMinutes(10)),
            AllowedPrivateUpstreamHosts = Read(configuration, "CANVAS_ALLOWED_PRIVATE_UPSTREAM_HOSTS"),
            AllowPrivateUpstreams = ReadBool(configuration, "CANVAS_ALLOW_PRIVATE_UPSTREAMS", false),
            WorkerConcurrency = ReadInt(configuration, "CANVAS_WORKER_CONCURRENCY", 3),
            ChannelConcurrency = ReadInt(configuration, "CANVAS_CHANNEL_CONCURRENCY", 3),
            WhisperBaseUrl = Read(configuration, "CANVAS_WHISPER_BASE_URL"),
            FfmpegPath = Read(configuration, "CANVAS_FFMPEG_PATH"),
            RedisUrl = Read(configuration, "REDIS_URL"),
        };
    }

    /// <summary>只读环境变量。保留给需要严格对齐 Go <c>os.Getenv</c> 语义的场景。</summary>
    public static CanvasEnvironment FromEnvironment() => FromConfiguration(
        new ConfigurationBuilder().AddEnvironmentVariables().Build());

    private static string? Read(IConfiguration configuration, string key)
    {
        string value = configuration[key] ?? string.Empty;
        return value.Length == 0 ? null : value;
    }

    private static bool ReadBool(IConfiguration configuration, string key, bool fallback)
    {
        string? value = Read(configuration, key);
        if (value is null)
        {
            return fallback;
        }

        string trimmed = value.Trim();
        if (bool.TryParse(trimmed, out bool parsed))
        {
            return parsed;
        }

        return trimmed switch
        {
            "1" => true,
            "0" => false,
            "t" or "T" => true,
            "f" or "F" => false,
            _ => throw new InvalidOperationException($"{key} 必须是 true 或 false"),
        };
    }

    private static TimeSpan ReadDuration(IConfiguration configuration, string key, TimeSpan fallback)
    {
        string? value = Read(configuration, key);
        if (value is null)
        {
            return fallback;
        }

        string trimmed = value.Trim();
        if (TryParseGoDuration(trimmed, out TimeSpan parsed) && parsed > TimeSpan.Zero)
        {
            return parsed;
        }

        throw new InvalidOperationException($"{key} 必须是正数时长，例如 10m");
    }

    private static int ReadInt(IConfiguration configuration, string key, int fallback)
    {
        string? value = Read(configuration, key);
        if (value is null)
        {
            return fallback;
        }

        return int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : throw new InvalidOperationException($"{key} 必须是整数");
    }

    /// <summary>对应 Go 的 <c>env(key, fallback)</c>：空串视为未设置。</summary>
    public static string Env(string key, string fallback)
    {
        string value = Environment.GetEnvironmentVariable(key) ?? string.Empty;
        return value.Length == 0 ? fallback : value;
    }

    /// <summary>对应 Go 的 <c>envBool</c>。</summary>
    public static bool EnvBool(string key, bool fallback)
    {
        string value = (Environment.GetEnvironmentVariable(key) ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return fallback;
        }

        if (bool.TryParse(value, out bool parsed))
        {
            return parsed;
        }

        // Go 用 strconv.ParseBool，接受 1/0/t/f/T/F/TRUE/FALSE 等写法。
        return value switch
        {
            "1" => true,
            "0" => false,
            "t" or "T" => true,
            "f" or "F" => false,
            _ => throw new InvalidOperationException($"{key} 必须是 true 或 false"),
        };
    }

    /// <summary>对应 Go 的 <c>envDuration</c>：必须是正数时长，例如 10m。</summary>
    public static TimeSpan EnvDuration(string key, TimeSpan fallback)
    {
        string value = (Environment.GetEnvironmentVariable(key) ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return fallback;
        }

        if (TryParseGoDuration(value, out TimeSpan parsed) && parsed > TimeSpan.Zero)
        {
            return parsed;
        }

        throw new InvalidOperationException($"{key} 必须是正数时长，例如 10m");
    }

    public static int EnvInt(string key, int fallback)
    {
        string value = (Environment.GetEnvironmentVariable(key) ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return fallback;
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : throw new InvalidOperationException($"{key} 必须是整数");
    }

    private static string? NullIfEmpty(string? value)
    {
        string trimmed = (value ?? string.Empty).Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>把 Go 的 <c>:8080</c> 写法转换为 Kestrel 可用的监听地址。</summary>
    private static string ToListenUrl(string address)
    {
        string value = address.Trim();
        if (value.StartsWith(':') && value.Length > 1)
        {
            return "http://0.0.0.0" + value;
        }

        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        return "http://" + value;
    }

    /// <summary>解析 Go 的 time.ParseDuration 语法（支持 ns/us/ms/s/m/h 复合）。</summary>
    private static bool TryParseGoDuration(string value, out TimeSpan result)
    {
        result = TimeSpan.Zero;
        string input = value;
        bool negative = false;
        if (input.StartsWith('-'))
        {
            negative = true;
            input = input[1..];
        }
        else if (input.StartsWith('+'))
        {
            input = input[1..];
        }

        if (input.Length == 0)
        {
            return false;
        }

        // 特例：Go 接受 "0"。
        if (input == "0")
        {
            return true;
        }

        double totalNanoseconds = 0;
        int index = 0;
        bool matchedAny = false;
        while (index < input.Length)
        {
            int start = index;
            while (index < input.Length && (char.IsAsciiDigit(input[index]) || input[index] == '.'))
            {
                index++;
            }

            if (start == index)
            {
                return false;
            }

            if (!double.TryParse(input[start..index], NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
            {
                return false;
            }

            int unitStart = index;
            while (index < input.Length && !char.IsAsciiDigit(input[index]) && input[index] != '.')
            {
                index++;
            }

            string unit = input[unitStart..index];
            double multiplier = unit switch
            {
                "ns" => 1d,
                "us" or "µs" or "μs" => 1_000d,
                "ms" => 1_000_000d,
                "s" => 1_000_000_000d,
                "m" => 60d * 1_000_000_000d,
                "h" => 3600d * 1_000_000_000d,
                _ => double.NaN,
            };

            if (double.IsNaN(multiplier))
            {
                return false;
            }

            totalNanoseconds += number * multiplier;
            matchedAny = true;
        }

        if (!matchedAny || totalNanoseconds > TimeSpan.MaxValue.TotalNanoseconds)
        {
            return false;
        }

        result = TimeSpan.FromTicks((long)(totalNanoseconds / 100d));
        if (negative)
        {
            result = result.Negate();
        }

        return true;
    }
}
