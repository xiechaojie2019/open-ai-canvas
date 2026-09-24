#nullable enable
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Persistence;
using OpenAICanvas.Persistence.Repositories;
using OpenAICanvas.Persistence.Schema;
using Xunit;

namespace OpenAICanvas.Tests.Persistence;

public class RepositoryAgentProfileTests : IDisposable
{
    private readonly string _databasePath;
    private readonly CanvasDatabase _database;
    private readonly Repository _repository;

    public RepositoryAgentProfileTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"canvas-agent-profile-{Guid.NewGuid():N}.db");
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
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }

        GC.SuppressFinalize(this);
    }

    private async Task MigrateAsync()
    {
        SchemaMigrator migrator = new(_database);
        await migrator.MigrateAsync();
    }

    private static AgentProfile NewProfile() => new()
    {
        ID = "AFP_000001",
        UserID = "USR_000001",
        Scope = "user",
        Content = "content",
        Revision = 1,
        Hash = "hash",
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task InsertThenReadBack()
    {
        await MigrateAsync();
        AgentProfile profile = NewProfile();

        AgentProfile? saved = await _repository.SaveAgentProfileAsync(profile, 0);

        Assert.NotNull(saved);
        AgentProfile? loaded = await _repository.AgentProfileForScopeAsync(
            profile.UserID, profile.Scope, profile.ProjectID, profile.CanvasID);
        Assert.NotNull(loaded);
        Assert.Equal(profile.ID, loaded!.ID);
    }
}
