#nullable enable
using System.Text.Json;
using OpenAICanvas.Application;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Platform;
using OpenAICanvas.Persistence;
using OpenAICanvas.Persistence.Repositories;
using OpenAICanvas.Persistence.Schema;
using Xunit;

namespace OpenAICanvas.Tests.Application;

/// <summary>
/// 生成媒体落盘：任务结果内联 dataUrl 解码为账号资源并把结果改写为资源引用。
/// 对应 Go: <c>resource_test.go</c> / <c>provider_test.go</c> 的
/// <c>persistGeneratedMediaResult</c> 用例（云 Agent 画布回写依赖此步产出资源）。
/// </summary>
public sealed class GeneratedMediaPersistTests : IAsyncDisposable
{
    private const string TinyPngDataUrl =
        "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    private readonly string _databasePath;
    private readonly string _dataDir;
    private readonly CanvasDatabase _database;
    private readonly Repository _repository;
    private readonly ResourceUploadService _upload;
    private readonly User _user = new() { ID = "u-gen", Username = "gen-user", Role = "user", Status = "active" };

    public GeneratedMediaPersistTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"canvas-gen-media-{Guid.NewGuid():N}.db");
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-gen-media-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
        _database = new CanvasDatabase(new CanvasDatabaseOptions
        {
            Driver = "sqlite",
            Dsn = $"Data Source={_databasePath};Pooling=False",
        });
        _repository = new Repository(_database);
        new SchemaMigrator(_database).MigrateAsync().GetAwaiter().GetResult();
        _repository.CreateAsync(_user).GetAwaiter().GetResult();
        _upload = new ResourceUploadService(
            _repository, new UploadQuota(_repository, new DefaultRuntimePolicyProvider()), _dataDir);
    }

    public async ValueTask DisposeAsync()
    {
        await _database.DisposeAsync().ConfigureAwait(false);
        try
        {
            Directory.Delete(_dataDir, recursive: true);
        }
        catch (IOException)
        {
            // Windows 文件句柄释放延迟，测试环境可容忍。
        }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task 无效DataURL_严格模式拒绝()
    {
        Dictionary<string, object?> result = new()
        {
            ["content"] = "data:video/mp4;base64,broken",
        };

        Exception error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _upload.PersistGeneratedMediaResultAsync("user-1", result)).ConfigureAwait(false);

        Assert.Contains("生成内容 data URL 无效", error.Message);
    }

    [Fact]
    public async Task 生成媒体按账号存储上限拦截()
    {
        // 默认策略 StoredFileGB=20：预占 20GB-1 字节后，1 字节生成物恰好触顶。
        await _repository.CreateAsync(new Resource
        {
            ID = "existing",
            UserID = "u-gen",
            Status = "ready",
            Provider = "local",
            ObjectKey = "objects/existing.bin",
            Size = (20L << 30) - 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        }).ConfigureAwait(false);
        Dictionary<string, object?> result = new()
        {
            ["image"] = new Dictionary<string, object?> { ["dataUrl"] = "data:image/png;base64,YQ==" },
        };

        Exception error = await Assert.ThrowsAsync<AppError>(
            () => _upload.PersistGeneratedMediaResultAsync("u-gen", result)).ConfigureAwait(false);

        Assert.Contains("20GB 上限", error.Message);
    }

    [Fact]
    public async Task dataUrl结果落库并改写为资源引用()
    {
        Dictionary<string, object?> result = new()
        {
            ["mode"] = "image",
            ["images"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["dataUrl"] = TinyPngDataUrl,
                    ["width"] = 0,
                    ["height"] = 0,
                },
            },
        };

        Dictionary<string, object?> stored = await _upload
            .PersistGeneratedMediaResultAsync("u-gen", result).ConfigureAwait(false);

        JsonElement images = Assert.IsType<JsonElement>(stored["images"]);
        JsonElement image = images[0];
        string resourceId = image.GetProperty("resourceId").GetString()!;
        Assert.True(resourceId.Length > 0, "resourceId 应非空");
        Assert.Equal("resource:" + resourceId, image.GetProperty("storageKey").GetString());
        string resourceUrl = image.GetProperty("dataUrl").GetString()!;
        Assert.Equal("/api/resources/" + resourceId + "/file", resourceUrl);
        Assert.Equal(resourceUrl, image.GetProperty("url").GetString());
        // 图片缺宽高时从解码数据补齐（1x1 PNG）。
        Assert.Equal(1, image.GetProperty("width").GetInt32());
        Assert.Equal(1, image.GetProperty("height").GetInt32());

        Resource? resource = await _repository.ResourceAsync(resourceId).ConfigureAwait(false);
        Assert.NotNull(resource);
        Assert.Equal("ready", resource!.Status);
        Assert.Equal("image", resource.Kind);
        Assert.Equal("image/png", resource.MimeType);
        Assert.Equal(
            Convert.FromBase64String(TinyPngDataUrl["data:image/png;base64,".Length..]).Length,
            resource.Size);
    }

    [Fact]
    public async Task 多张内联图片全部落库()
    {
        Dictionary<string, object?> result = new()
        {
            ["mode"] = "image",
            ["images"] = new List<object?>
            {
                new Dictionary<string, object?> { ["dataUrl"] = TinyPngDataUrl },
                new Dictionary<string, object?> { ["dataUrl"] = TinyPngDataUrl },
            },
        };

        Dictionary<string, object?> stored = await _upload
            .PersistGeneratedMediaResultAsync("u-gen", result).ConfigureAwait(false);

        JsonElement images = Assert.IsType<JsonElement>(stored["images"]);
        Assert.Equal(2, images.GetArrayLength());
        string first = images[0].GetProperty("resourceId").GetString()!;
        string second = images[1].GetProperty("resourceId").GetString()!;
        Assert.True(first.Length > 0 && second.Length > 0, "两张图都应落库");
        Assert.NotEqual(first, second);
        Assert.StartsWith("/api/resources/", images[1].GetProperty("dataUrl").GetString());
    }

    [Fact]
    public async Task 无内联媒体时结果原样返回()
    {
        Dictionary<string, object?> result = new()
        {
            ["mode"] = "text",
            ["text"] = "纯文本结果",
        };

        Dictionary<string, object?> stored = await _upload
            .PersistGeneratedMediaResultAsync("u-gen", result).ConfigureAwait(false);

        // 原样返回：不做 JSON 往返，保留原始 CLR 类型（下游 ResultText 依赖 string）。
        Assert.Same(result, stored);
        Assert.Equal("纯文本结果", Assert.IsType<string>(stored["text"]));
    }
}
