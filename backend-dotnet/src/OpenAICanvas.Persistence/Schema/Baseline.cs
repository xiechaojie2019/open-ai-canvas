using System.Data.Common;
using System.Text.Json;
using Dapper;
using OpenAICanvas.Domain.Model;

namespace OpenAICanvas.Persistence.Schema;

/// <summary>
/// 迁移步骤实现。对应 Go: internal/database/schema.go 与 migrations.go 的各
/// <c>migrateSchemaV*</c> 函数。
/// </summary>
public static class Baseline
{
    /// <summary>
    /// 应用完整建表脚本（全部表与索引，均带 IF NOT EXISTS，可重复执行）。
    /// </summary>
    /// <remarks>
    /// 对应 Go 里以 <c>AutoMigrate</c> 为主的迁移步骤（v5/v7/v10/v11/v13/v15）。
    /// 对已存在的表，<c>CREATE TABLE IF NOT EXISTS</c> 会整体跳过——因此这些版本的
    /// 新增列必须由各自的 ALTER 步骤单独补齐。
    /// </remarks>
    public static async Task ApplyFullSchemaAsync(SchemaMigrationContext ctx)
    {
        await ctx.ExecuteAsync(ctx.Scripts.For(ctx.Dialect)).ConfigureAwait(false);
    }

    /// <summary>
    /// 版本 1：基线结构。对应 Go: <c>migrateSchemaV1</c>。
    /// </summary>
    /// <remarks>
    /// Go 的 v1 还包含若干"pre-v1 遗留结构"的数据搬迁（渠道模型价格档回填、
    /// 供应线路渠道模型映射、物理可用配置清理）。这些只在早于 v1 基线的库上生效，
    /// 而 v1 已是当前发布的基线，因此这里不重复实现；如确需升级此类历史库，
    /// 应先用 Go 版 <c>migrate-schema up</c> 升到 v1 以上。
    /// </remarks>
    public static async Task ApplyAsync(SchemaMigrationContext ctx)
    {
        // PostgreSQL 的 Migrator 跳过主键列变更，素材 ID 扩容必须在建表前显式执行。
        await WidenPostgresAssetIdColumnsAsync(ctx).ConfigureAwait(false);

        // 逻辑删除后的同名模型允许重新添加，旧唯一索引不能继续覆盖已删除记录。
        foreach (string legacyIndex in new[]
        {
            "idx_channel_model_key",
            "idx_users_email",
            "idx_route_attempt_task_number",
            "idx_logical_model_source_active",
        })
        {
            await ctx.ExecuteAsync($"DROP INDEX IF EXISTS {legacyIndex}").ConfigureAwait(false);
        }

        await ApplyFullSchemaAsync(ctx).ConfigureAwait(false);

        // 为升级前已存在的逻辑模型回填版本序列，避免首次保存时从 0 重新分配。
        await ctx.ExecuteAsync(
            "UPDATE logical_models SET revision_sequence = COALESCE((SELECT MAX(version) FROM logical_model_revisions WHERE logical_model_id = logical_models.id), 0) WHERE revision_sequence = 0")
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 版本 3：资产候选身份收敛。对应 Go: <c>migrateSchemaV3</c>。
    /// </summary>
    public static async Task ApplyAssetTaxonomyAsync(SchemaMigrationContext ctx)
    {
        await ApplyFullSchemaAsync(ctx).ConfigureAwait(false);

        foreach (string column in new[] { "name_key", "source" })
        {
            await ctx.AddColumnIfMissingAsync(
                "project_asset_candidates", column, ctx.ColumnDefinition("project_asset_candidates", column))
                .ConfigureAwait(false);
        }

        await ctx.ExecuteAsync(
            "UPDATE assets SET category = 'prop' WHERE category IN ('wardrobe', 'weapon', 'accessory')").ConfigureAwait(false);
        await ctx.ExecuteAsync(
            "UPDATE assets SET category = 'material' WHERE category = 'style' OR (category = 'other' AND kind IN ('image', 'video', 'audio', 'model'))").ConfigureAwait(false);
        await ctx.ExecuteAsync(
            "UPDATE project_asset_candidates SET category = 'prop' WHERE category IN ('wardrobe', 'weapon', 'accessory')").ConfigureAwait(false);
        await ctx.ExecuteAsync(
            "UPDATE project_asset_candidates SET category = 'material' WHERE category = 'style'").ConfigureAwait(false);

        // 回填归一化名称，并把项目内重复的待确认候选标记为 ignored（保留最早记录）。
        IReadOnlyList<CandidateRow> candidates = await ctx.QueryAsync<CandidateRow>(
            "SELECT id, project_id, category, status, name FROM project_asset_candidates ORDER BY created_at ASC, id ASC")
            .ConfigureAwait(false);

        Dictionary<string, string> seenPending = new(StringComparer.Ordinal);
        foreach (CandidateRow candidate in candidates)
        {
            string nameKey = ModelText.AssetCandidateNameKey(candidate.Name ?? string.Empty);
            string identity = $"{candidate.ProjectID}:{candidate.Category}:{nameKey}";
            string? status = null;

            if (candidate.Status == "pending_confirmation" && nameKey.Length > 0)
            {
                if (seenPending.ContainsKey(identity))
                {
                    status = "ignored";
                }
                else
                {
                    seenPending[identity] = candidate.ID;
                }
            }

            await ctx.ExecuteAsync(
                status is null
                    ? "UPDATE project_asset_candidates SET name_key = @nameKey WHERE id = @id"
                    : "UPDATE project_asset_candidates SET name_key = @nameKey, status = @status WHERE id = @id",
                new { nameKey, status, id = candidate.ID }).ConfigureAwait(false);
        }

        await ctx.ExecuteAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS idx_project_asset_candidates_pending_identity ON project_asset_candidates(project_id, category, name_key) WHERE status = 'pending_confirmation' AND name_key <> ''")
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 版本 14：Agent 运行恢复控制。对应 Go: <c>migrateSchemaV14</c>。
    /// </summary>
    public static async Task ApplyCloudAgentRecoveryAsync(SchemaMigrationContext ctx)
    {
        await ApplyFullSchemaAsync(ctx).ConfigureAwait(false);

        foreach (string column in new[]
        {
            "canvas_id", "active_task_id", "media_task_id", "cleanup_pending", "failure_message",
        })
        {
            await ctx.AddColumnIfMissingAsync(
                "cloud_agent_executions", column, ctx.ColumnDefinition("cloud_agent_executions", column))
                .ConfigureAwait(false);
        }

        // 逐批回填，保持迁移事务的全有全无语义，同时限制内存占用。
        string after = string.Empty;
        while (true)
        {
            IReadOnlyList<ExecutionRow> runs = await ctx.QueryAsync<ExecutionRow>(
                "SELECT id, status, state_json FROM cloud_agent_executions WHERE id > @after AND status <> 'completed' ORDER BY id ASC LIMIT 100",
                new { after }).ConfigureAwait(false);

            if (runs.Count == 0)
            {
                return;
            }

            foreach (ExecutionRow run in runs)
            {
                // 根任务 ID 始终是安全的取消锚点。旧记录损坏时保留根任务并写入告警，
                // 由恢复流程取消根任务，而不是阻塞整个部署。
                string activeTaskId = run.ID;
                string? canvasId = null;
                string? mediaTaskId = null;
                string? failureMessage = null;

                if (!TryReadExecutionState(run.StateJSON, out string? parsedCanvasId, out string? parsedActiveTaskId, out string? parsedMediaTaskId))
                {
                    failureMessage = "旧 Agent 运行记录损坏，已保留根任务并进入安全收尾；请核对任务中心";
                }
                else
                {
                    canvasId = parsedCanvasId;
                    mediaTaskId = parsedMediaTaskId;
                    if (!string.IsNullOrEmpty(parsedActiveTaskId))
                    {
                        activeTaskId = parsedActiveTaskId;
                    }
                }

                bool cleanupPending = run.Status is "cancelled" or "failed";

                await ctx.ExecuteAsync(
                    "UPDATE cloud_agent_executions SET canvas_id = @canvasId, active_task_id = @activeTaskId, media_task_id = @mediaTaskId, cleanup_pending = @cleanupPending, failure_message = COALESCE(@failureMessage, failure_message) WHERE id = @id",
                    new
                    {
                        canvasId,
                        activeTaskId,
                        mediaTaskId,
                        cleanupPending = ctx.Dialect.Boolean(cleanupPending),
                        failureMessage,
                        id = run.ID,
                    }).ConfigureAwait(false);

                after = run.ID;
            }
        }
    }

    private static bool TryReadExecutionState(
        string? stateJson,
        out string? canvasId,
        out string? activeTaskId,
        out string? mediaTaskId)
    {
        canvasId = null;
        activeTaskId = null;
        mediaTaskId = null;

        if (string.IsNullOrWhiteSpace(stateJson))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(stateJson);
            JsonElement root = document.RootElement;

            if (root.TryGetProperty("request", out JsonElement request)
                && request.TryGetProperty("canvasId", out JsonElement canvas))
            {
                canvasId = canvas.GetString();
            }

            if (root.TryGetProperty("activeTaskId", out JsonElement active))
            {
                activeTaskId = active.GetString();
            }

            if (root.TryGetProperty("mediaTaskId", out JsonElement media))
            {
                mediaTaskId = media.GetString();
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// PostgreSQL 下把素材 ID 相关列扩容到 <c>varchar(80)</c>。
    /// 对应 Go: <c>widenPostgresAssetIDColumns</c>。
    /// </summary>
    private static async Task WidenPostgresAssetIdColumnsAsync(SchemaMigrationContext ctx)
    {
        if (!ctx.Dialect.IsPostgres)
        {
            return;
        }

        (string Table, string Column)[] targets =
        [
            ("assets", "id"),
            ("project_asset_links", "asset_id"),
            ("project_asset_candidates", "resolved_asset_id"),
            ("asset_versions", "asset_id"),
        ];

        foreach ((string table, string column) in targets)
        {
            long? length = await ctx.ScalarAsync<long?>(
                "SELECT character_maximum_length FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = @table AND column_name = @column",
                new { table, column }).ConfigureAwait(false);

            if (length is null || length >= ModelText.AssetIdMaxLength)
            {
                continue;
            }

            await ctx.ExecuteAsync(
                $"ALTER TABLE \"{table}\" ALTER COLUMN \"{column}\" TYPE varchar({ModelText.AssetIdMaxLength})")
                .ConfigureAwait(false);
        }
    }

    private sealed class CandidateRow
    {
        public string ID { get; init; } = string.Empty;

        public string ProjectID { get; init; } = string.Empty;

        public string Category { get; init; } = string.Empty;

        public string Status { get; init; } = string.Empty;

        public string? Name { get; init; }
    }

    private sealed class ExecutionRow
    {
        public string ID { get; init; } = string.Empty;

        public string Status { get; init; } = string.Empty;

        public string? StateJSON { get; init; }
    }
}
