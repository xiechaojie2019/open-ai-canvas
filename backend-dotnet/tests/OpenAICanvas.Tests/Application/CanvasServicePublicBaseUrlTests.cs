#nullable enable
using OpenAICanvas.Application;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence;
using OpenAICanvas.Persistence.Repositories;
using OpenAICanvas.Persistence.Schema;
using Xunit;

namespace OpenAICanvas.Tests.Application;

/// <summary>
/// CanvasService 组合根接线测试：worker 路径的 ResourceDomain 必须能读到平台公网地址，
/// 否则本地存储的 provider-input 签发报“未配置服务器访问地址”。
/// 对应 Go: <c>publicResourceBaseURL</c> 读 platform OSS setting。
/// </summary>
public sealed class CanvasServicePublicBaseUrlTests : IAsyncDisposable
{
    private readonly string _databasePath;
    private readonly string _dataDir;
    private readonly CanvasDatabase _database;
    private readonly Repository _repository;

    public CanvasServicePublicBaseUrlTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"canvas-baseurl-{Guid.NewGuid():N}.db");
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-baseurl-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
        _database = new CanvasDatabase(new CanvasDatabaseOptions
        {
            Driver = "sqlite",
            Dsn = $"Data Source={_databasePath};Pooling=False",
        });
        _repository = new Repository(_database);
        new SchemaMigrator(_database).MigrateAsync().GetAwaiter().GetResult();
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
        }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task 组合根_ResourceDomain_能读到平台公网地址()
    {
        await _repository.SaveSystemSettingAsync(new SystemSetting
        {
            Key = "oss",
            // 与测试站一致：http 地址 + enabled=false（公网地址读取不依赖 enabled）。
            ValueJSON = """
                {"enabled":false,"provider":"aliyun","publicBaseUrl":"https://example.com","pathPrefix":"open-ai-canvas"}
                """,
            UpdatedBy = "admin",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        CanvasService canvas = new(_repository, dataDir: _dataDir);

        Resource resource = new()
        {
            ID = IdGenerator.NewId(),
            UserID = "u-base",
            Kind = "image",
            Status = ResourceStatus.ResourceStatusReady,
            Provider = "local",
            ObjectKey = "users/u-base/image/2026/09/30/a.png",
            MimeType = "image/png",
            Size = 8,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        await _repository.CreateResourceAsync(resource);

        // provider-input 场景：要求绝对 URL（组合根必须能解析平台公网地址）。
        string url = await canvas.ResourceDomain.ProviderResourceUrlAsync(
            resource, DateTime.UtcNow.AddMinutes(5));
        Assert.StartsWith("https://example.com/api/public/resources/", url);
        Assert.Contains("signature=", url);
    }
}
