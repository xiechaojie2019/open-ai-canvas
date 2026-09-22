#nullable enable

using System.IO.Compression;
using System.Text;
using OpenAICanvas.Application;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence;
using OpenAICanvas.Persistence.Repositories;
using OpenAICanvas.Persistence.Schema;
using OpenAICanvas.Protocol;
using Xunit;

namespace OpenAICanvas.Tests.Application;

/// <summary>
/// 插件运行时（10.1/10.2）：bootstrap 数量、包安装/卸载往返与内置保护。
/// </summary>
public sealed class PluginRuntimeTests : IDisposable
{
    private readonly string _databasePath;
    private readonly string _dataDir;
    private readonly CanvasDatabase _database;
    private readonly Repository _repository;
    private readonly string? _originalPluginDir;

    public PluginRuntimeTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"canvas-plugin-runtime-{Guid.NewGuid():N}.db");
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-plugin-runtime-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
        _database = new CanvasDatabase(new CanvasDatabaseOptions
        {
            Driver = "sqlite",
            Dsn = $"Data Source={_databasePath}",
        });
        _repository = new Repository(_database);
        _originalPluginDir = Environment.GetEnvironmentVariable("CANVAS_OFFICIAL_PLUGIN_DIR");
        Environment.SetEnvironmentVariable("CANVAS_OFFICIAL_PLUGIN_DIR", FindRepoPluginPackages());
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CANVAS_OFFICIAL_PLUGIN_DIR", _originalPluginDir);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
        if (Directory.Exists(_dataDir))
        {
            try
            {
                Directory.Delete(_dataDir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
        GC.SuppressFinalize(this);
    }

    private static string FindRepoPluginPackages()
    {
        string? current = AppContext.BaseDirectory;
        for (int depth = 0; depth < 10 && current is not null; depth++)
        {
            string candidate = Path.Combine(current, "plugin-packages");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
            current = Path.GetDirectoryName(current);
        }
        throw new InvalidOperationException("找不到仓库 plugin-packages 目录");
    }

    private PluginRuntime CreateRuntime() => new(_repository, _dataDir);

    private async Task MigrateAsync()
    {
        SchemaMigrator migrator = new(_database);
        await migrator.MigrateAsync();
    }

    /// <summary>构造一个最小合法的声明式工作流插件包（zip + manifest.json）。</summary>
    private static byte[] BuildPackage(string pluginId, string workflowId = "demo-image")
    {
        string manifest = $$"""
{
  "apiVersion": "yingce.plugin/v1",
  "id": "{{pluginId}}",
  "version": "1.0.0",
  "name": "Demo Plugin",
  "surfaces": ["node"],
  "permissions": ["generation.run"],
  "contributes": {
    "workflows": [
      {
        "id": "{{workflowId}}",
        "label": "Demo",
        "providerId": "{{pluginId}}",
        "capability": "image",
        "parameters": []
      }
    ]
  }
}
""";
        using MemoryStream output = new();
        using (ZipArchive archive = new(output, ZipArchiveMode.Create))
        {
            ZipArchiveEntry entry = archive.CreateEntry("manifest.json");
            // 直接写 UTF-8 字节，避免 StreamWriter 引入 BOM（解码端要求裸 JSON）。
            using (Stream stream = entry.Open())
            {
                byte[] bytes = Encoding.UTF8.GetBytes(manifest);
                stream.Write(bytes, 0, bytes.Length);
            }
        }
        return output.ToArray();
    }

    [Fact]
    public async Task Bootstrap_注册官方包与内置清单()
    {
        await MigrateAsync();
        PluginRuntime runtime = CreateRuntime();
        await runtime.BootstrapAsync();

        List<PluginView> plugins = runtime.List();
        int officialCount = Directory.GetFiles(FindRepoPluginPackages(), "*.yingce-plugin").Length;
        // bundled 固定补充 1 个工作流清单；两个支付插件官方目录已提供同名包，不再重复入表。
        Assert.Equal(officialCount + 1, plugins.Count);
        Assert.Contains(plugins, plugin => plugin.Manifest.ID == WorkflowPluginGate.RunningHub
            && plugin.Source == "bundled");
        Assert.Contains(plugins, plugin => plugin.Manifest.ID == "official-payment-wechat-native");
        // 工作流插件有可执行贡献，协议注册表能解析；bundled 默认 disabled 不注册。
        ProtocolAdapterRegistry registry = runtime.RegistrySnapshot();
        Assert.NotNull(registry);
    }

    [Fact]
    public async Task 安装_启用_卸载往返()
    {
        await MigrateAsync();
        PluginRuntime runtime = CreateRuntime();
        await runtime.BootstrapAsync();

        byte[] package = BuildPackage("demo-test-plugin");
        PluginView installed = await runtime.InstallAsync(package, "demo.yingce-plugin");
        Assert.Equal("demo-test-plugin", installed.Manifest.ID);
        Assert.Equal("uploaded", installed.Source);
        Assert.Equal("enabled", installed.Status);
        Assert.True(File.Exists(Path.Combine(_dataDir, "plugin-packages", installed.Sha256 + ".yingce-plugin")));

        Assert.Contains(runtime.List(), plugin => plugin.Manifest.ID == "demo-test-plugin");

        string? removedPath = await runtime.UninstallAsync("demo-test-plugin");
        Assert.NotNull(removedPath);
        Assert.False(File.Exists(removedPath));
        Assert.DoesNotContain(runtime.List(), plugin => plugin.Manifest.ID == "demo-test-plugin");
    }

    [Fact]
    public async Task 内置插件不能上传覆盖也不能卸载()
    {
        await MigrateAsync();
        PluginRuntime runtime = CreateRuntime();
        await runtime.BootstrapAsync();

        byte[] package = BuildPackage(WorkflowPluginGate.RunningHub);
        AppError installError = await Assert.ThrowsAsync<AppError>(
            () => runtime.InstallAsync(package, "override.yingce-plugin"));
        Assert.Contains("不能通过上传覆盖", installError.Message);

        AppError uninstallError = await Assert.ThrowsAsync<AppError>(
            () => runtime.UninstallAsync(WorkflowPluginGate.RunningHub));
        Assert.Contains("不能卸载", uninstallError.Message);
    }

    [Fact]
    public async Task 协议注册表包含官方插件快照()
    {
        await MigrateAsync();
        PluginRuntime runtime = CreateRuntime();
        await runtime.BootstrapAsync();

        ProtocolAdapterRegistry registry = runtime.RegistrySnapshot();
        List<Metadata> items = registry.List(includeUnavailable: true);
        // 官方包目录中的声明式 provider 应出现在快照中（bundled workflow 是 trusted-backend 不注册）。
        Assert.NotEmpty(items);
    }
}
