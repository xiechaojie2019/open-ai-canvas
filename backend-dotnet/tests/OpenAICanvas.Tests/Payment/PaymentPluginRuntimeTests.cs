#nullable enable

using OpenAICanvas.Application;
using OpenAICanvas.Payment;
using OpenAICanvas.Persistence;
using OpenAICanvas.Persistence.Repositories;
using Xunit;

namespace OpenAICanvas.Tests.Payment;

/// <summary>
/// 官方支付包到 .NET 支付注册表的运行时契约测试。
/// </summary>
public sealed class PaymentPluginRuntimeTests : IDisposable
{
    private readonly string _dataDir;
    private readonly string _databasePath;
    private readonly CanvasDatabase _database;
    private readonly Repository _repository;

    public PaymentPluginRuntimeTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-payment-runtime-{Guid.NewGuid():N}");
        _databasePath = Path.Combine(Path.GetTempPath(), $"canvas-payment-runtime-{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(_dataDir);
        _database = new CanvasDatabase(new CanvasDatabaseOptions
        {
            Driver = "sqlite",
            Dsn = $"Data Source={_databasePath}",
        });
        _repository = new Repository(_database);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
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
        if (File.Exists(_databasePath))
        {
            try
            {
                File.Delete(_databasePath);
            }
            catch (IOException)
            {
            }
        }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Bootstrap_注册官方支付宝和微信RPC适配器并物化运行文件()
    {
        PluginRuntime runtime = new(_repository, _dataDir);

        await runtime.BootstrapAsync();

        Assert.NotNull(runtime.PaymentRegistry.Get(PaymentPluginManifests.ProviderAlipay));
        Assert.NotNull(runtime.PaymentRegistry.Get(PaymentPluginManifests.ProviderWeChat));
        Assert.Contains(runtime.PaymentRegistry.Descriptors(), item =>
            item.ID == PaymentPluginManifests.ProviderAlipay && item.PluginID == PaymentPluginManifests.PluginAlipayPage);
        Assert.Contains(runtime.PaymentRegistry.Descriptors(), item =>
            item.ID == PaymentPluginManifests.ProviderWeChat && item.PluginID == PaymentPluginManifests.PluginWeChatNative);

        foreach (string pluginID in new[]
                 {
                     PaymentPluginManifests.PluginAlipayPage,
                     PaymentPluginManifests.PluginWeChatNative,
                 })
        {
            PluginView plugin = Assert.Single(runtime.List().Where(item => item.Manifest.ID == pluginID));
            Assert.Equal("enabled", plugin.Status);
            string executable = Path.Combine(_dataDir, "plugin-packages", "runtime", plugin.Sha256, "backend", "provider");
            Assert.True(File.Exists(executable), executable);
        }

        // 官方包当前随仓库发布为 Linux ELF；Windows 只验证包校验、物化和注册，
        // Linux/同架构 CI 再执行真实 Go 插件的配置校验。
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // 调用真实 Go 插件的配置校验，证明 registry 中不是仅有描述符的假 provider。
        PaymentProviderException error = await Assert.ThrowsAsync<PaymentProviderException>(
            () => runtime.PaymentRegistry.Get(PaymentPluginManifests.ProviderAlipay)!
                .ValidateConfigAsync(new Dictionary<string, string>()));
        Assert.Contains("支付宝配置缺少", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 禁用支付插件后注册表清空且重启保持禁用()
    {
        PluginRuntime runtime = new(_repository, _dataDir);
        await runtime.BootstrapAsync();

        await runtime.SetEnabledAsync(PaymentPluginManifests.PluginAlipayPage, enabled: false);

        Assert.Null(runtime.PaymentRegistry.Get(PaymentPluginManifests.ProviderAlipay));
        Assert.NotNull(runtime.PaymentRegistry.Get(PaymentPluginManifests.ProviderWeChat));
        Assert.Equal("disabled", Assert.Single(runtime.List()
            .Where(item => item.Manifest.ID == PaymentPluginManifests.PluginAlipayPage)).Status);

        PluginRuntime restarted = new(_repository, _dataDir);
        await restarted.BootstrapAsync();

        Assert.Null(restarted.PaymentRegistry.Get(PaymentPluginManifests.ProviderAlipay));
        Assert.NotNull(restarted.PaymentRegistry.Get(PaymentPluginManifests.ProviderWeChat));
    }
}
