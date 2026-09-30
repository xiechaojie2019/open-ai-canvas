#nullable enable
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenAICanvas.Application;
using OpenAICanvas.Persistence;
using OpenAICanvas.Persistence.Repositories;
using OpenAICanvas.Persistence.Schema;
using Xunit;

namespace OpenAICanvas.Tests.Application;

/// <summary>
/// 场景预设与内置技能种子的契约测试。
/// 对应 Go: <c>skills_presets.go</c> 的校验语义与 <c>EnsureBuiltinSkills</c> 幂等落库。
/// </summary>
public sealed class SkillPresetsTests : IAsyncDisposable
{
    private readonly string _databasePath;
    private readonly string _dataDir;
    private readonly CanvasDatabase _database;
    private readonly Repository _repository;
    private readonly SkillsService _service;

    public SkillPresetsTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"canvas-presets-{Guid.NewGuid():N}.db");
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-presets-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
        _database = new CanvasDatabase(new CanvasDatabaseOptions
        {
            Driver = "sqlite",
            Dsn = $"Data Source={_databasePath};Pooling=False",
        });
        _repository = new Repository(_database);
        new SchemaMigrator(_database).MigrateAsync().GetAwaiter().GetResult();
        _service = new SkillsService(_repository, _dataDir);
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
    public void 预设目录_通过校验且引用种子技能()
    {
        List<SkillPresetDto> presets = _service.SkillPresets();
        Assert.NotEmpty(presets);
        foreach (SkillPresetDto preset in presets)
        {
            Assert.Matches("^[a-z0-9]+(-[a-z0-9]+)*$", preset.PresetID);
            Assert.InRange(preset.SkillIDs.Count, 1, 8);
            Assert.Equal("hand-curated", preset.Source);
            Assert.Contains(preset.Evidence, new[] { "E1", "E2", "E3", "E4", "E5" });
        }
        // 场景分类合法。
        Assert.All(presets, preset => Assert.Contains(preset.Scene,
            new[] { "drama", "creative", "ecommerce", "social", "others" }));
    }

    [Fact]
    public async Task 内置技能_幂等落库且重复同步不清空关系()
    {
        await _service.EnsureBuiltinSkillsAsync().ConfigureAwait(false);
        SkillListDto first = await _service.SkillsAsync("", 1, 60, "", "", "", "popular", CancellationToken.None)
            .ConfigureAwait(false);
        Assert.NotEmpty(first.Skills);
        // 幂等：重复种子不报错、条数不翻倍。
        await _service.EnsureBuiltinSkillsAsync().ConfigureAwait(false);
        SkillListDto second = await _service.SkillsAsync("", 1, 60, "", "", "", "popular", CancellationToken.None)
            .ConfigureAwait(false);
        Assert.Equal(first.Skills.Count, second.Skills.Count);
        // 图片编辑三技能在列（首 60 条按 popular 排序；不足时翻页取全部核对）。
        List<SkillItemDto> all = [.. second.Skills];
        for (int page = 2; second.HasMore && page <= 50; page++)
        {
            SkillListDto next = await _service.SkillsAsync("", page, 60, "", "", "", "popular", CancellationToken.None)
                .ConfigureAwait(false);
            all.AddRange(next.Skills);
            if (!next.HasMore)
            {
                break;
            }
        }
        Assert.Contains(all, skill => skill.SkillID == "yingce-image-editing");
        Assert.Contains(all, skill => skill.SkillID == "yingce-image-annotation");
        Assert.Contains(all, skill => skill.SkillID == "yingce-image-layer-split");
    }
}
