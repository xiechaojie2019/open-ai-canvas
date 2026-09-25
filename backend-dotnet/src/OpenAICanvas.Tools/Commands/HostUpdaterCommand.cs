using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OpenAICanvas.Tools.Commands;

/// <summary>
/// 对应 Go <c>cmd/host-updater</c> 的最小安全协议宿主。
/// 当前 .NET 部署仍由 Docker 镜像切换，更新和回滚动作明确拒绝，不伪装成已完成的切换器。
/// </summary>
internal static class HostUpdaterCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("host-updater 仅支持 Linux Unix domain socket 部署");
        }

        string socketPath = Read("CANVAS_UPDATER_SOCKET", "/run/open-ai-canvas-updater/updater.sock");
        string token = (Environment.GetEnvironmentVariable("CANVAS_UPDATER_TOKEN") ?? string.Empty).Trim();
        if (token.Length < 32)
        {
            throw new InvalidOperationException("CANVAS_UPDATER_TOKEN 至少需要 32 个字符");
        }

        string stateDir = Read("CANVAS_UPDATER_STATE_DIR", "/var/lib/open-ai-canvas-updater");
        string installDir = Read("CANVAS_UPDATER_INSTALL_DIR", "/opt/open-ai-canvas");
        string repository = Read("CANVAS_UPDATER_REPOSITORY", "ddcat-ai/open-ai-canvas");
        Directory.CreateDirectory(Path.GetDirectoryName(socketPath) ?? ".");
        Directory.CreateDirectory(stateDir);
        if (File.Exists(socketPath))
        {
            throw new InvalidOperationException($"Unix socket 已存在，请确认没有运行中的 host-updater：{socketPath}");
        }

        UpdaterState state = await UpdaterState.LoadAsync(Path.Combine(stateDir, "state.json"), cancellationToken)
            .ConfigureAwait(false);
        UpdaterContext context = new(repository, installDir, state);

        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],
            ApplicationName = typeof(HostUpdaterCommand).Assembly.GetName().Name,
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.ListenUnixSocket(socketPath));
        WebApplication app = builder.Build();

        app.Use(async (httpContext, next) =>
        {
            httpContext.Response.Headers.CacheControl = "no-store";
            if (!Authorized(httpContext, token))
            {
                await WriteErrorAsync(httpContext, StatusCodes.Status401Unauthorized, "更新器认证失败")
                    .ConfigureAwait(false);
                return;
            }
            if (httpContext.Request.ContentLength is > 64 * 1024)
            {
                await WriteErrorAsync(httpContext, StatusCodes.Status400BadRequest, "请求体超过 64KB")
                    .ConfigureAwait(false);
                return;
            }
            await next(httpContext).ConfigureAwait(false);
        });

        app.MapGet("/v1/status", () => Results.Json(context.Snapshot(), JsonOptions));
        app.MapPost("/v1/check", async () =>
        {
            try
            {
                await context.CheckAsync(cancellationToken).ConfigureAwait(false);
                return Results.Json(context.Snapshot(), JsonOptions);
            }
            catch (Exception error)
            {
                return Results.Json(new { error = SafeError(error), data = context.Snapshot() }, JsonOptions,
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });
        app.MapPost("/v1/update", () => Results.Json(
            new { error = "当前 .NET 部署使用 Docker 镜像，请通过 docker compose pull/up 完成升级", data = context.Snapshot() },
            JsonOptions,
            statusCode: StatusCodes.Status409Conflict));
        app.MapPost("/v1/rollback", () => Results.Json(
            new { error = "当前 .NET 部署不支持在线回滚，请通过镜像版本和数据库备份完成降级", data = context.Snapshot() },
            JsonOptions,
            statusCode: StatusCodes.Status409Conflict));

        app.Lifetime.ApplicationStarted.Register(() =>
        {
            try
            {
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                        UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
                }
            }
            catch
            {
                // Socket 权限失败会在部署日志中可见，但不应阻止 Kestrel 完成启动。
            }
        });

        try
        {
            await app.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await app.DisposeAsync().ConfigureAwait(false);
            TryDeleteSocket(socketPath);
        }
        return 0;
    }

    private static bool Authorized(HttpContext context, string token)
    {
        string provided = context.Request.Headers.Authorization.ToString();
        if (provided.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            provided = provided[7..].Trim();
        }
        byte[] left = System.Text.Encoding.UTF8.GetBytes(provided);
        byte[] right = System.Text.Encoding.UTF8.GetBytes(token);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }

    private static async Task WriteErrorAsync(HttpContext context, int statusCode, string message)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(new { error = message, data = (object?)null }, JsonOptions)
            .ConfigureAwait(false);
    }

    private static string Read(string key, string fallback)
    {
        string value = (Environment.GetEnvironmentVariable(key) ?? string.Empty).Trim();
        return value.Length == 0 ? fallback : value;
    }

    private static string SafeError(Exception error)
    {
        string message = error.Message.Trim();
        return message.Length > 1200 ? message[..1200] : message;
    }

    private static void TryDeleteSocket(string socketPath)
    {
        try
        {
            if (File.Exists(socketPath)) File.Delete(socketPath);
        }
        catch
        {
            // 进程退出时不再抛出清理异常，避免掩盖原始服务错误。
        }
    }

    private sealed class UpdaterContext(string repository, string installDir, UpdaterState state)
    {
        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
        private readonly string _statePath = state.Path;
        private readonly UpdaterState _state = state;
        private readonly string _repository = repository;
        private readonly string _installDir = installDir;

        public UpdaterStatus Snapshot()
        {
            string current = ReadCurrentVersion();
            return new UpdaterStatus
            {
                Supported = true,
                Connected = true,
                Repository = _repository,
                Deployment = "docker-compose-host-updater",
                CurrentVersion = current,
                LatestRelease = _state.LatestRelease,
                UpdateAvailable = _state.LatestRelease is not null && CompareVersions(current, _state.LatestRelease.Version) < 0,
                Checks =
                [
                    new UpdaterCheck { Key = "updater", Label = "Host Updater", Status = "passed", Detail = "dotnet/linux", Blocking = true },
                    new UpdaterCheck { Key = "version", Label = "当前版本", Status = current.Length == 0 ? "failed" : "passed", Detail = current, Blocking = true },
                    new UpdaterCheck { Key = "deployment", Label = "部署方式", Status = "passed", Detail = "docker-compose", Blocking = true },
                ],
                RollbackVersion = _state.RollbackVersion,
                Operation = _state.Operation,
            };
        }

        public async Task CheckAsync(CancellationToken cancellationToken)
        {
            using HttpRequestMessage request = new(HttpMethod.Get,
                $"https://api.github.com/repos/{_repository}/releases?per_page=30");
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("open-ai-canvas-host-updater", "1"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using JsonDocument document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
            ReleaseInfo? latest = null;
            foreach (JsonElement item in document.RootElement.EnumerateArray())
            {
                string tag = item.TryGetProperty("tag_name", out JsonElement tagValue) ? tagValue.GetString() ?? "" : "";
                bool draft = item.TryGetProperty("draft", out JsonElement draftValue) && draftValue.GetBoolean();
                if (draft || !tag.StartsWith('v')) continue;
                ReleaseInfo candidate = new()
                {
                    Version = tag,
                    Name = item.TryGetProperty("name", out JsonElement name) ? name.GetString() ?? tag : tag,
                    Body = item.TryGetProperty("body", out JsonElement releaseBody) ? releaseBody.GetString() ?? "" : "",
                    Url = item.TryGetProperty("html_url", out JsonElement url) ? url.GetString() ?? "" : "",
                    Prerelease = item.TryGetProperty("prerelease", out JsonElement prerelease) && prerelease.GetBoolean(),
                };
                if (latest is null || CompareVersions(candidate.Version, latest.Version) > 0) latest = candidate;
            }
            if (latest is null) throw new InvalidOperationException("GitHub 尚未发布可用 Release");
            _state.LatestRelease = latest;
            _state.Operation = new UpdaterOperation { Phase = CompareVersions(ReadCurrentVersion(), latest.Version) < 0 ? "ready" : "no_update", Logs = [] };
            await _state.SaveAsync(_statePath, cancellationToken).ConfigureAwait(false);
        }

        private string ReadCurrentVersion()
        {
            string path = Path.Combine(_installDir, Read("CANVAS_UPDATER_ENV_FILE", ".env"));
            if (!File.Exists(path)) return "";
            foreach (string line in File.ReadLines(path))
            {
                string trimmed = line.Trim();
                if (!trimmed.StartsWith("CANVAS_IMAGE_TAG=", StringComparison.Ordinal)) continue;
                string value = trimmed["CANVAS_IMAGE_TAG=".Length..].Trim().Trim('"');
                return value.Length > 0 && value != "latest" && !value.StartsWith('v') ? "v" + value : value;
            }
            return "";
        }
    }

    private sealed class UpdaterState
    {
        public UpdaterState(string path) => Path = path;
        public string Path { get; }
        public ReleaseInfo? LatestRelease { get; set; }
        public string RollbackVersion { get; set; } = "";
        public UpdaterOperation Operation { get; set; } = new();

        public static async Task<UpdaterState> LoadAsync(string path, CancellationToken cancellationToken)
        {
            UpdaterState state = new(path);
            if (!File.Exists(path)) return state;
            try
            {
                await using FileStream stream = File.OpenRead(path);
                PersistedState? persisted = await JsonSerializer.DeserializeAsync<PersistedState>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
                if (persisted is not null)
                {
                    state.LatestRelease = persisted.LatestRelease;
                    state.RollbackVersion = persisted.RollbackVersion;
                    state.Operation = persisted.Operation ?? new UpdaterOperation();
                }
                return state;
            }
            catch (JsonException error)
            {
                throw new InvalidOperationException("解析更新器状态失败", error);
            }
        }

        public async Task SaveAsync(string path, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path) ?? ".");
            string temporary = path + ".tmp";
            await using (FileStream stream = File.Create(temporary))
            {
                await JsonSerializer.SerializeAsync(stream, new PersistedState
                {
                    LatestRelease = LatestRelease,
                    RollbackVersion = RollbackVersion,
                    Operation = Operation,
                }, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, path, true);
        }
    }

    private sealed class PersistedState
    {
        public ReleaseInfo? LatestRelease { get; set; }
        public string RollbackVersion { get; set; } = "";
        public UpdaterOperation? Operation { get; set; }
    }

    private sealed class UpdaterStatus
    {
        public bool Supported { get; init; }
        public bool Connected { get; init; }
        public string Repository { get; init; } = "";
        public string Deployment { get; init; } = "";
        public string CurrentVersion { get; init; } = "";
        public ReleaseInfo? LatestRelease { get; init; }
        public bool UpdateAvailable { get; init; }
        public IReadOnlyList<UpdaterCheck> Checks { get; init; } = [];
        public string RollbackVersion { get; init; } = "";
        public UpdaterOperation Operation { get; init; } = new();
    }

    private sealed class ReleaseInfo
    {
        public string Version { get; init; } = "";
        public string Name { get; init; } = "";
        public string Body { get; init; } = "";
        public string Url { get; init; } = "";
        public bool Prerelease { get; init; }
    }

    private sealed class UpdaterCheck
    {
        public string Key { get; init; } = "";
        public string Label { get; init; } = "";
        public string Status { get; init; } = "";
        public string Detail { get; init; } = "";
        public bool Blocking { get; init; }
    }

    private sealed class UpdaterOperation
    {
        public string ID { get; init; } = "";
        public string Phase { get; init; } = "idle";
        public string FromVersion { get; init; } = "";
        public string TargetVersion { get; init; } = "";
        public DateTimeOffset? StartedAt { get; init; }
        public DateTimeOffset? FinishedAt { get; init; }
        public string Error { get; init; } = "";
        public string RollbackError { get; init; } = "";
        public bool AutomaticRollback { get; init; }
        public IReadOnlyList<object> Logs { get; init; } = [];
    }

    private static int CompareVersions(string left, string right)
    {
        string a = left.Trim().TrimStart('v');
        string b = right.Trim().TrimStart('v');
        if (Version.TryParse(a, out Version? av) && Version.TryParse(b, out Version? bv)) return av.CompareTo(bv);
        return string.CompareOrdinal(a, b);
    }
}
