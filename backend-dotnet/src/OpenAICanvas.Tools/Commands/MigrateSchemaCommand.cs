using OpenAICanvas.Persistence.Schema;

namespace OpenAICanvas.Tools.Commands;

/// <summary>对应 Go <c>cmd/migrate-schema</c>。</summary>
internal static class MigrateSchemaCommand
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        string command = args.Length > 1 ? args[1].Trim().ToLowerInvariant() : "up";
        if (command is not ("up" or "status" or "verify"))
        {
            throw new InvalidOperationException($"未知 migrate-schema 命令 {command}；可用命令：up、status、verify");
        }

        await using ToolDatabase tools = ToolDatabase.Open();
        SchemaMigrator migrator = new(tools.Database);
        switch (command)
        {
            case "up":
                await migrator.MigrateAsync(cancellationToken).ConfigureAwait(false);
                break;
            case "verify":
                await migrator.RequireSchemaVersionAsync(cancellationToken).ConfigureAwait(false);
                break;
        }

        SchemaStatus status = await migrator.ReadStatusAsync(cancellationToken).ConfigureAwait(false);
        ToolEnvironment.PrintJson(status);
        return 0;
    }
}
