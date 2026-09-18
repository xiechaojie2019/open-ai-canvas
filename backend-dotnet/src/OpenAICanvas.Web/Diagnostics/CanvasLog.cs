using Microsoft.Extensions.Logging;

namespace OpenAICanvas.Web.Diagnostics;

/// <summary>
/// 等价于 Go 的包级 <c>log</c>。用于在静态 helper 中记录日志，
/// 需要结构化日志的场景仍应注入 <see cref="ILogger{T}"/>。
/// </summary>
public static class CanvasLog
{
    private static ILoggerFactory? _factory;
    private static ILogger? _logger;

    public static void Configure(ILoggerFactory factory)
    {
        _factory = factory;
        _logger = factory.CreateLogger("OpenAICanvas");
    }

    public static void Warn(string message) => (_logger ?? NullLogger.Instance).LogWarning("{Message}", message);

    public static void Error(string message) => (_logger ?? NullLogger.Instance).LogError("{Message}", message);

    public static void Info(string message) => (_logger ?? NullLogger.Instance).LogInformation("{Message}", message);

    private static class NullLogger
    {
        internal static readonly ILogger Instance = LoggerFactory.Create(_ => { }).CreateLogger("OpenAICanvas.Null");
    }
}
