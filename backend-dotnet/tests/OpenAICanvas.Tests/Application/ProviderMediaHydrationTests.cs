#nullable enable
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using OpenAICanvas.Application;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Domain.Serialization;
using OpenAICanvas.Persistence;
using OpenAICanvas.Persistence.Repositories;
using OpenAICanvas.Persistence.Schema;
using OpenAICanvas.Platform;
using OpenAICanvas.Providers;
using TaskEntity = OpenAICanvas.Domain.Entities.Task;
using Xunit;

namespace OpenAICanvas.Tests.Application;

/// <summary>
/// 参考素材水合（Go provider.go 的 hydrateGenerationMedia）：
/// resource: 引用 → 字节 data URL（multipart 协议）/ URL（URL 优先协议），
/// 归属校验、就绪校验、读取上限。
/// </summary>
public sealed class ProviderMediaHydrationTests : IDisposable
{
    private readonly string _databasePath;
    private readonly string _dataDir;
    private readonly CanvasDatabase _database;
    private readonly Repository _repository;
    private readonly ResourceDomainService _domain;

    public ProviderMediaHydrationTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"canvas-hydrate-{Guid.NewGuid():N}.db");
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-hydrate-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
        _database = new CanvasDatabase(new CanvasDatabaseOptions
        {
            Driver = "sqlite",
            Dsn = $"Data Source={_databasePath};Pooling=False",
        });
        _repository = new Repository(_database);
        _domain = new ResourceDomainService(_repository, new DefaultRuntimePolicyProvider(), _dataDir);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
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
        SchemaMigrator migrator = new(_database);
        await migrator.MigrateAsync();
    }

    private async Task SeedReadyResourceAsync(string id, byte[] bytes, string mime)
    {
        string objectKey = "objects/" + id + ".bin";
        await using (SqliteConnection connection = (SqliteConnection)_database.CreateConnection())
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                "INSERT INTO resources (id, user_id, kind, status, provider, object_key, mime_type, size, width, height, duration_ms, created_at, updated_at) " +
                "VALUES (@id, 'user-a', 'image', 'ready', 'local', @objectKey, @mime, @size, 100, 50, 0, datetime('now'), datetime('now'))",
                new { id, objectKey, mime, size = (long)bytes.Length });
        }
        string root = Path.Combine(_dataDir, "resources");
        Directory.CreateDirectory(Path.GetDirectoryName(root + "/" + objectKey)!);
        await File.WriteAllBytesAsync(Path.Combine(root, objectKey), bytes);
    }

    private static TextTaskInput InputWith(string storageKey) => new()
    {
        Mode = "image",
        Config = new ProviderConfig
        {
            ChannelID = "CHANNEL_000001",
            Model = "gpt-image-2",
            InterfaceType = "openai-image",
        },
        ReferenceImages =
        [
            new ProviderMedia
            {
                ID = "ref-1",
                StorageKey = storageKey,
                MIMEType = "image/png",
            },
        ],
    };

    private static readonly byte[] PngBytes =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4,
    ];

    [Fact]
    public async Task 字节路径_resource引用解析为dataURL()
    {
        await MigrateAsync();
        await SeedReadyResourceAsync("res-hyd-1", PngBytes, "image/png");

        TextTaskInput input = InputWith("resource:res-hyd-1");
        await ProviderMediaHydrator.HydrateGenerationMediaAsync(
            "user-a", input, _repository, _domain, RuntimePolicySetting.Default);

        ProviderMedia media = input.ReferenceImages[0];
        Assert.StartsWith("data:image/png;base64,", media.DataURL, StringComparison.Ordinal);
        Assert.Equal(PngBytes.Length, media.Bytes);
        Assert.Equal(string.Empty, media.URL);
    }

    [Fact]
    public async Task 非resource引用与未就绪资源()
    {
        await MigrateAsync();
        // URL 形式直通（openai-image 非 URL 优先协议，无 storageKey → 不动）。
        TextTaskInput passthrough = InputWith("");
        passthrough.ReferenceImages[0].DataURL = "data:image/png;base64,AAAA";
        passthrough.ReferenceImages[0].URL = "https://upstream/a.png";
        await ProviderMediaHydrator.HydrateGenerationMediaAsync(
            "user-a", passthrough, _repository, _domain, RuntimePolicySetting.Default);
        Assert.Equal("https://upstream/a.png", passthrough.ReferenceImages[0].URL);

        // 未就绪资源拒绝。
        await SeedReadyResourceAsync("res-hyd-2", PngBytes, "image/png");
        await using (SqliteConnection connection = (SqliteConnection)_database.CreateConnection())
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync("UPDATE resources SET status = 'pending' WHERE id = 'res-hyd-2'");
        }
        TextTaskInput pending = InputWith("resource:res-hyd-2");
        AppError pendingError = await Assert.ThrowsAsync<AppError>(
            () => ProviderMediaHydrator.HydrateGenerationMediaAsync(
                "user-a", pending, _repository, _domain, RuntimePolicySetting.Default));
        Assert.Contains("尚未上传完成", pendingError.Message, StringComparison.Ordinal);

        // 他人资源拒绝（归属校验）。
        TextTaskInput missing = InputWith("resource:res-unknown");
        AppError missingError = await Assert.ThrowsAsync<AppError>(
            () => ProviderMediaHydrator.HydrateGenerationMediaAsync(
                "user-a", missing, _repository, _domain, RuntimePolicySetting.Default));
        Assert.NotEmpty(missingError.Message);
    }

    [Fact]
    public async Task URL优先协议解析为签名地址_但HTTP公网被拒()
    {
        await MigrateAsync();
        await SeedReadyResourceAsync("res-hyd-3", PngBytes, "image/png");
        await using (SqliteConnection connection = (SqliteConnection)_database.CreateConnection())
        {
            await connection.OpenAsync();
            // 切到对象存储 provider 以命中 useObjectURL 分支。
            await connection.ExecuteAsync("UPDATE resources SET provider = 's3' WHERE id = 'res-hyd-3'");
        }

        TextTaskInput input = InputWith("resource:res-hyd-3");
        input.Config.InterfaceType = "newapi"; // requireURL 协议
        // 公网地址未配置 HTTPS → provider URL 报错（与 Go 行为一致）。
        AppError urlError = await Assert.ThrowsAsync<AppError>(
            () => ProviderMediaHydrator.HydrateGenerationMediaAsync(
                "user-a", input, _repository, _domain, RuntimePolicySetting.Default));
        Assert.Contains("服务器访问地址", urlError.Message, StringComparison.Ordinal);

        // requireURL 协议 + 非 resource 引用携带 dataURL → 显式拒绝（resource: 引用走 URL 解析）。
        TextTaskInput embedded = InputWith("");
        embedded.Config.InterfaceType = "newapi";
        embedded.ReferenceImages[0].DataURL = "data:image/png;base64,AAAA";
        AppError embeddedError = await Assert.ThrowsAsync<AppError>(
            () => ProviderMediaHydrator.HydrateGenerationMediaAsync(
                "user-a", embedded, _repository, _domain, RuntimePolicySetting.Default));
        Assert.Contains("不能使用内嵌数据", embeddedError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HasResourceReferences检测()
    {
        TextTaskInput none = new();
        Assert.False(ProviderMediaHydrator.HasResourceReferences(none));

        TextTaskInput withRef = InputWith("resource:res-1");
        Assert.True(ProviderMediaHydrator.HasResourceReferences(withRef));

        TextTaskInput urlRef = InputWith("");
        urlRef.ReferenceImages[0].StorageKey = "";
        Assert.False(ProviderMediaHydrator.HasResourceReferences(urlRef));
    }
}
