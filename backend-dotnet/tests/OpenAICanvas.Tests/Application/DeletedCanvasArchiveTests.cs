#nullable enable
using OpenAICanvas.Application;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence;
using OpenAICanvas.Persistence.Repositories;
using OpenAICanvas.Persistence.Schema;
using Xunit;
using TaskEntity = OpenAICanvas.Domain.Entities.Task;

namespace OpenAICanvas.Tests.Application;

/// <summary>
/// 删除画布时在途任务的产物补登：删除路径给排队/运行中的任务打归档标记，
/// 任务完成侧据此把结果写进素材库，已结束的任务不受影响。
/// </summary>
public sealed class DeletedCanvasArchiveTests : IAsyncDisposable
{
    private const string CanvasID = "canvas-archive-1";
    private const string CanvasTitle = "补登测试画布";

    private readonly string _databasePath;
    private readonly string _dataDir;
    private readonly CanvasDatabase _database;
    private readonly Repository _repository;
    private readonly CanvasService _canvas;
    private readonly User _user = new()
    {
        ID = "u-archive",
        Username = "archive-user",
        Role = "user",
        Status = "active",
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    public DeletedCanvasArchiveTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"canvas-archive-{Guid.NewGuid():N}.db");
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-archive-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
        _database = new CanvasDatabase(new CanvasDatabaseOptions
        {
            Driver = "sqlite",
            Dsn = $"Data Source={_databasePath};Pooling=False",
        });
        _repository = new Repository(_database);
        new SchemaMigrator(_database).MigrateAsync().GetAwaiter().GetResult();
        _repository.CreateAsync(_user).GetAwaiter().GetResult();
        _canvas = new CanvasService(_repository, dataDir: _dataDir);
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

    [Theory]
    [InlineData("running", true)]
    [InlineData("queued", true)]
    [InlineData("text_replay", true)]
    [InlineData("succeeded", false)]
    public async Task 删除画布时只给在途任务打归档标记(string status, bool marked)
    {
        await SeedAsync(status);

        await _repository.DeleteCanvasProjectAsync(_user.ID, CanvasID).ConfigureAwait(false);

        TaskEntity task = (await _repository.TaskAsync("task-archive-1").ConfigureAwait(false))!;
        Assert.Equal("", task.ProjectID);
        Assert.Equal(marked, TaskArchiveMarker.TryRead(task.InputJSON, out _, out _));
        Assert.Equal("node-1", AssertNodeID(task.InputJSON));
    }

    [Fact]
    public async Task 已标记任务完成后产物写入素材库()
    {
        await SeedAsync("running");
        await _repository.DeleteCanvasProjectAsync(_user.ID, CanvasID).ConfigureAwait(false);
        TaskEntity task = (await _repository.TaskAsync("task-archive-1").ConfigureAwait(false))!;
        task.ResultJSON = """
            {"mode":"image","images":[{"url":"/api/resources/res-1/file","width":1024,"height":1024,"bytes":2048,"mimeType":"image/png"}]}
            """;

        await _canvas.AssetArchive.SaveAsync(task).ConfigureAwait(false);

        Asset asset = (await _repository.AssetForUserAsync(_user.ID, "asset-task-task-archive-1-0")
            .ConfigureAwait(false))!;
        Assert.NotNull(asset);
        Assert.Equal("image", asset.Kind);
        using System.Text.Json.JsonDocument payload =
            System.Text.Json.JsonDocument.Parse(asset.PayloadJSON);
        Assert.Equal($"已删除画布：{CanvasTitle}",
            payload.RootElement.GetProperty("source").GetString());
        Assert.Equal(CanvasID,
            payload.RootElement.GetProperty("metadata").GetProperty("deletedCanvasId").GetString());
        Assert.Equal("node-1",
            payload.RootElement.GetProperty("metadata").GetProperty("nodeId").GetString());
    }

    [Fact]
    public async Task 未标记任务完成不写素材库()
    {
        await SeedAsync("succeeded");
        TaskEntity task = (await _repository.TaskAsync("task-archive-1").ConfigureAwait(false))!;
        task.ResultJSON = """
            {"mode":"image","images":[{"url":"/api/resources/res-1/file","width":1024,"height":1024,"bytes":2048,"mimeType":"image/png"}]}
            """;

        await _canvas.AssetArchive.SaveAsync(task).ConfigureAwait(false);

        Assert.Null(await _repository.AssetForUserAsync(_user.ID, "asset-task-task-archive-1-0")
            .ConfigureAwait(false));
    }

    private async Task SeedAsync(string status)
    {
        await _repository.CreateCanvasProjectAsync(new CanvasProject
        {
            ID = CanvasID,
            UserID = _user.ID,
            Title = CanvasTitle,
            PayloadJSON = "{}",
            Revision = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        }).ConfigureAwait(false);
        await _repository.CreateAsync(new TaskEntity
        {
            ID = "task-archive-1",
            UserID = _user.ID,
            ProjectID = CanvasID,
            Type = "canvas_image",
            Status = status,
            InputJSON = """
                {"prompt":"宝石改成粉色","metadata":{"nodeId":"node-1","sourceNodeId":"node-0"}}
                """,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        }).ConfigureAwait(false);
    }

    private static object AssertNodeID(string inputJSON)
    {
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(inputJSON);
        return document.RootElement.GetProperty("metadata").GetProperty("nodeId").GetString()!;
    }
}
