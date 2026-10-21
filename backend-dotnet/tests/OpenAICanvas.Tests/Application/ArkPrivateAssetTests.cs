#nullable enable
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using OpenAICanvas.Application;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence;
using OpenAICanvas.Persistence.Repositories;
using OpenAICanvas.Persistence.Schema;
using OpenAICanvas.Providers;
using Xunit;

namespace OpenAICanvas.Tests.Application;

/// <summary>
/// 方舟私域素材库：仅 Seedance 视频参考图上传到可信素材库并改写为 asset://。
/// 覆盖开关、模型边界、建组复用、审核轮询和失败恢复。
/// </summary>
public sealed class ArkPrivateAssetTests : IDisposable
{
    private const string UserID = "user-a";
    private const string ProjectName = "canvas-project";

    private readonly string _databasePath;
    private readonly string _dataDir;
    private readonly CanvasDatabase _database;
    private readonly Repository _repository;
    private readonly PlatformSettingsService _settings;

    public ArkPrivateAssetTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"canvas-ark-assets-{Guid.NewGuid():N}.db");
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-ark-assets-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
        _database = new CanvasDatabase(new CanvasDatabaseOptions
        {
            Driver = "sqlite",
            Dsn = $"Data Source={_databasePath};Pooling=False",
        });
        _repository = new Repository(_database);
        _settings = new PlatformSettingsService(_repository, _dataDir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
        GC.SuppressFinalize(this);
    }

    private async Task MigrateAsync()
    {
        await new SchemaMigrator(_database).MigrateAsync();
    }

    private async Task SeedReadyResourceAsync(string id, string mime = "image/png")
    {
        await using SqliteConnection connection = (SqliteConnection)_database.CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync(
            "INSERT INTO resources (id, \"userId\", kind, status, provider, \"objectKey\", \"mimeType\", size, width, height, \"durationMs\", \"createdAt\", \"updatedAt\") " +
            "VALUES (@id, @userID, 'image', 'ready', 'local', @objectKey, @mime, 4, 100, 50, 0, datetime('now'), datetime('now'))",
            new { id, userID = UserID, objectKey = "objects/" + id + ".bin", mime });
    }

    private async Task SeedSettingAsync(
        bool enabled = true,
        string region = "cn-beijing",
        string? accessKeyID = "test-ak",
        string? accessKeySecret = "test-sk",
        string projectName = ProjectName)
    {
        string value = JsonSerializer.Serialize(new
        {
            enabled,
            region,
            projectName,
            accessKeyId = accessKeyID ?? "",
            accessKeySecret = accessKeySecret ?? "",
            defaultGroupId = "",
        });
        await using SqliteConnection connection = (SqliteConnection)_database.CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync(
            "INSERT INTO \"systemSettings\" (\"key\", \"valueJson\", \"updatedBy\", \"createdAt\", \"updatedAt\") " +
            "VALUES ('ark_private_assets', @value, @updatedBy, datetime('now'), datetime('now'))",
            new { value, updatedBy = UserID });
    }

    private ArkPrivateAssetService CreateService(HttpMessageHandler handler) => new(
        _repository,
        _settings,
        (resource, _, _) => Task.FromResult($"https://canvas.example/resources/{resource.ID}?signature=test"),
        new HttpClient(handler),
        controlPlaneBaseUrl: "https://ark.cn-beijing.volces.com",
        pollInterval: TimeSpan.FromMilliseconds(1),
        pollTimeout: TimeSpan.FromSeconds(2));

    private static TextTaskInput InputWith(
        string mode,
        string model,
        string interfaceType,
        params string[] resourceIds) => new()
        {
            Mode = mode,
            Config = new ProviderConfig
            {
                ChannelID = "CHANNEL_000001",
                Model = model,
                InterfaceType = interfaceType,
            },
            ReferenceImages = [.. resourceIds.Select((id, index) => new ProviderMedia
            {
                ID = $"ref-{index}",
                StorageKey = "resource:" + id,
                MIMEType = "image/png",
            })],
        };

    [Fact]
    public void 只有Seedance视频与受管图片引用需要准备素材()
    {
        TextTaskInput seedance = InputWith("video", "doubao-seedance-1-0-pro", "volcengine-ark-video", "res-1");
        Assert.True(ArkPrivateAssetService.RequiresPreparation(seedance));

        TextTaskInput agentPlan = InputWith(
            "video", "doubao-seedance-1-0-lite", "volcengine-ark-agent-plan-video", "res-1");
        Assert.True(ArkPrivateAssetService.RequiresPreparation(agentPlan));

        Assert.False(ArkPrivateAssetService.RequiresPreparation(
            InputWith("video", "doubao-seed-1-6", "volcengine-ark-video", "res-1")));
        Assert.False(ArkPrivateAssetService.RequiresPreparation(
            InputWith("image", "doubao-seedance-1-0-pro", "volcengine-ark-image", "res-1")));
        Assert.False(ArkPrivateAssetService.RequiresPreparation(
            InputWith("video", "doubao-seedance-1-0-pro", "openai-video", "res-1")));
        Assert.False(ArkPrivateAssetService.RequiresPreparation(
            InputWith("video", "doubao-seedance-1-0-pro", "volcengine-ark-video")));

        TextTaskInput disabled = InputWith("video", "doubao-seedance-1-0-pro", "volcengine-ark-video", "res-1");
        disabled.Config.ArkPrivateAssetUpload = "false";
        Assert.False(ArkPrivateAssetService.RequiresPreparation(disabled));
    }

    [Fact]
    public async Task 非Seedance任务不访问控制面且保留原参考图()
    {
        await MigrateAsync();
        await SeedSettingAsync();
        await SeedReadyResourceAsync("res-1");
        StubHandler handler = new();
        ArkPrivateAssetService service = CreateService(handler);

        TextTaskInput input = InputWith("video", "doubao-seed-1-6", "volcengine-ark-video", "res-1");
        input.ReferenceImages[0].URL = "https://upstream/reference.png";
        await service.PrepareVideoReferencesAsync(UserID, input);

        Assert.Empty(handler.Requests);
        Assert.Equal("https://upstream/reference.png", input.ReferenceImages[0].URL);
    }

    [Fact]
    public async Task 已启用时上传参考图并写入asset协议()
    {
        await MigrateAsync();
        await SeedSettingAsync();
        await SeedReadyResourceAsync("res-1");
        StubHandler handler = new(request => request.Action switch
        {
            "CreateAssetGroup" => StubHandler.Json("{\"Result\":{\"Id\":\"group-1\"}}"),
            "CreateAsset" => StubHandler.Json("{\"Result\":{\"Id\":\"asset-1\"}}"),
            "GetAsset" => StubHandler.Json("{\"Result\":{\"Status\":\"Active\"}}"),
            _ => StubHandler.Json("{}", HttpStatusCode.BadRequest),
        });
        ArkPrivateAssetService service = CreateService(handler);

        TextTaskInput input = InputWith("video", "doubao-seedance-1-0-pro", "volcengine-ark-video", "res-1");
        input.ReferenceImages[0].URL = "https://canvas.example/old.png";
        input.ReferenceImages[0].DataURL = "data:image/png;base64,AAAA";
        await service.PrepareVideoReferencesAsync(UserID, input);

        Assert.Equal("asset://asset-1", input.ReferenceImages[0].URL);
        Assert.Equal("", input.ReferenceImages[0].DataURL);
        Assert.Equal(["CreateAssetGroup", "CreateAsset", "GetAsset"], handler.Actions);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal("POST", request.Method);
            Assert.Equal(1, request.Uri.Query.Count(character => character == '?'));
            Assert.Equal("2024-01-01", request.QueryValue("Version"));
            Assert.Contains("Action", request.Uri.Query, StringComparison.Ordinal);
            Assert.True(request.HasAuthorization);
        });
        Assert.Contains("\"URL\":\"https://canvas.example/resources/res-1?signature=test\"", handler.Requests[1].Body);

        ArkPrivateAssetBinding binding = Assert.IsType<ArkPrivateAssetBinding>(
            await _repository.ArkPrivateAssetBindingAsync("res-1", ProjectName));
        Assert.Equal("active", binding.Status);
        Assert.Equal("group-1", binding.AssetGroupID);
        Assert.Equal("asset-1", binding.ArkAssetID);

        ArkPrivateAssetConnection saved = await _settings.ReadArkPrivateAssetConnectionAsync();
        Assert.Equal("group-1", saved.DefaultGroupID);

        // 已 active 的绑定不再重复上传，也不重新建组。
        await service.PrepareVideoReferencesAsync(UserID, input);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task 多张参考图只建一次组且每张图只上传一次()
    {
        await MigrateAsync();
        await SeedSettingAsync();
        await SeedReadyResourceAsync("res-1");
        await SeedReadyResourceAsync("res-2");
        int created = 0;
        StubHandler handler = new(request => request.Action switch
        {
            "CreateAssetGroup" => StubHandler.Json("{\"Result\":{\"GroupId\":\"group-1\"}}"),
            "CreateAsset" => StubHandler.Json($"{{\"Result\":{{\"AssetId\":\"asset-{++created}\"}}}}"),
            "GetAsset" => StubHandler.Json("{\"Result\":{\"Status\":\"Active\"}}"),
            _ => StubHandler.Json("{}", HttpStatusCode.BadRequest),
        });
        ArkPrivateAssetService service = CreateService(handler);

        TextTaskInput input = InputWith(
            "video", "doubao-seedance-1-0-pro", "volcengine-ark-video", "res-1", "res-2");
        await service.PrepareVideoReferencesAsync(UserID, input);

        Assert.Equal("asset://asset-1", input.ReferenceImages[0].URL);
        Assert.Equal("asset://asset-2", input.ReferenceImages[1].URL);
        Assert.Equal(1, handler.Actions.Count(action => action == "CreateAssetGroup"));
        Assert.Equal(2, handler.Actions.Count(action => action == "CreateAsset"));
        Assert.Equal(2, handler.Actions.Count(action => action == "GetAsset"));
    }

    [Fact]
    public async Task 未启用时不发请求并保留原图()
    {
        await MigrateAsync();
        await SeedSettingAsync(enabled: false);
        await SeedReadyResourceAsync("res-1");
        StubHandler handler = new();
        ArkPrivateAssetService service = CreateService(handler);

        TextTaskInput input = InputWith("video", "doubao-seedance-1-0-pro", "volcengine-ark-video", "res-1");
        input.ReferenceImages[0].URL = "https://canvas.example/reference.png";
        await service.PrepareVideoReferencesAsync(UserID, input);

        Assert.Empty(handler.Requests);
        Assert.Equal("https://canvas.example/reference.png", input.ReferenceImages[0].URL);
        Assert.Null(await _repository.ArkPrivateAssetBindingAsync("res-1", ProjectName));
    }

    [Fact]
    public async Task 启用但缺少密钥时明确失败()
    {
        await MigrateAsync();
        await SeedSettingAsync(accessKeySecret: "");
        await SeedReadyResourceAsync("res-1");
        StubHandler handler = new();
        ArkPrivateAssetService service = CreateService(handler);

        TextTaskInput input = InputWith("video", "doubao-seedance-1-0-pro", "volcengine-ark-video", "res-1");
        AppError error = await Assert.ThrowsAsync<AppError>(
            () => service.PrepareVideoReferencesAsync(UserID, input));

        Assert.Contains("IAM AK/SK", error.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task 非法Region在联网前被拒绝且绑定记录失败()
    {
        await MigrateAsync();
        await SeedSettingAsync(region: "cn-beijing.evil");
        await SeedReadyResourceAsync("res-1");
        StubHandler handler = new();
        ArkPrivateAssetService service = new(
            _repository,
            _settings,
            (resource, _, _) => Task.FromResult($"https://canvas.example/resources/{resource.ID}"),
            new HttpClient(handler),
            controlPlaneBaseUrl: null,
            pollInterval: TimeSpan.FromMilliseconds(1),
            pollTimeout: TimeSpan.FromSeconds(1));

        TextTaskInput input = InputWith("video", "doubao-seedance-1-0-pro", "volcengine-ark-video", "res-1");
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.PrepareVideoReferencesAsync(UserID, input));

        Assert.Contains("Region 格式无效", error.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
        ArkPrivateAssetBinding binding = Assert.IsType<ArkPrivateAssetBinding>(
            await _repository.ArkPrivateAssetBindingAsync("res-1", ProjectName));
        Assert.Equal("failed", binding.Status);
    }

    [Fact]
    public async Task 控制面上游错误原样透传()
    {
        await MigrateAsync();
        await SeedSettingAsync();
        await SeedReadyResourceAsync("res-1");
        StubHandler handler = new(_ => StubHandler.Json(
            "{\"ResponseMetadata\":{\"Error\":{\"Code\":\"AccessDenied\",\"Message\":\"denied by iam\"}}}",
            HttpStatusCode.Forbidden));
        ArkPrivateAssetService service = CreateService(handler);

        TextTaskInput input = InputWith("video", "doubao-seedance-1-0-pro", "volcengine-ark-video", "res-1");
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.PrepareVideoReferencesAsync(UserID, input));

        Assert.Contains("HTTP 403", error.Message, StringComparison.Ordinal);
        Assert.Contains("AccessDenied", error.Message, StringComparison.Ordinal);
        ArkPrivateAssetBinding binding = Assert.IsType<ArkPrivateAssetBinding>(
            await _repository.ArkPrivateAssetBindingAsync("res-1", ProjectName));
        Assert.Equal("failed", binding.Status);
        Assert.Contains("AccessDenied", binding.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 旧轮询参数错误后恢复查询而不重复上传()
    {
        await MigrateAsync();
        await SeedSettingAsync();
        await SeedReadyResourceAsync("res-1");
        await using (SqliteConnection connection = (SqliteConnection)_database.CreateConnection())
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                "INSERT INTO \"arkPrivateAssetBindings\" (\"id\", \"userId\", \"resourceId\", \"projectName\", \"assetGroupId\", \"arkAssetId\", \"status\", \"error\", \"createdAt\", \"updatedAt\") " +
                "VALUES ('binding-1', @userID, 'res-1', @projectName, 'group-1', 'asset-1', 'failed', 'MissingParameter.Id', datetime('now'), datetime('now'))",
                new { userID = UserID, projectName = ProjectName });
        }
        StubHandler handler = new(request => request.Action == "GetAsset"
            ? StubHandler.Json("{\"Result\":{\"Status\":\"Active\"}}")
            : StubHandler.Json("{}", HttpStatusCode.BadRequest));
        ArkPrivateAssetService service = CreateService(handler);

        TextTaskInput input = InputWith("video", "doubao-seedance-1-0-pro", "volcengine-ark-video", "res-1");
        await service.PrepareVideoReferencesAsync(UserID, input);

        Assert.Equal("asset://asset-1", input.ReferenceImages[0].URL);
        Assert.Equal(["GetAsset"], handler.Actions);
        ArkPrivateAssetBinding binding = Assert.IsType<ArkPrivateAssetBinding>(
            await _repository.ArkPrivateAssetBindingAsync("res-1", ProjectName));
        Assert.Equal("active", binding.Status);
    }

    [Fact]
    public void Seedance请求体接受asset协议()
    {
        string url = ProviderHelpers.SeedanceImageInputURL(new ProviderMedia
        {
            URL = "asset://asset-1",
        });
        Assert.Equal("asset://asset-1", url);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<CapturedRequest, HttpResponseMessage> _respond;

        public StubHandler(Func<CapturedRequest, HttpResponseMessage>? respond = null)
        {
            _respond = respond ?? (_ => throw new InvalidOperationException("不应发起控制面请求"));
        }

        public List<CapturedRequest> Requests { get; } = [];

        public List<string> Actions => [.. Requests.Select(request => request.Action)];

        public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);
            CapturedRequest captured = new(
                request.Method.Method,
                request.RequestUri ?? throw new InvalidOperationException("缺少 URI"),
                body,
                request.Headers.TryGetValues("Authorization", out IEnumerable<string>? authorization)
                    && authorization.Any());
            Requests.Add(captured);
            return _respond(captured);
        }
    }

    private sealed record CapturedRequest(string Method, Uri Uri, string Body, bool HasAuthorization)
    {
        public string Action => QueryValue("Action");

        public string QueryValue(string key)
        {
            foreach (string pair in Uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = pair.Split('=', 2);
                if (parts.Length == 2 && parts[0] == key)
                {
                    return Uri.UnescapeDataString(parts[1]);
                }
            }
            return "";
        }
    }
}
