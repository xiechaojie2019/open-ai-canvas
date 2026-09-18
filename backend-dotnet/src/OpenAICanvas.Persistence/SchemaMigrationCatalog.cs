using OpenAICanvas.Persistence.Schema;

namespace OpenAICanvas.Persistence;

/// <summary>
/// 迁移目录。版本号、名称与校验和与 Go 的 <c>database.schemaMigrations</c> 逐字一致——
/// 这决定了 .NET 版能否接管 Go 版已迁移过的数据库。
/// </summary>
/// <remarks>
/// 对应 Go: internal/database/migrations.go 的 <c>schemaMigrations</c> 与
/// <c>migrationsForDatabase</c>（历史版本 6/7 顺序特例）。
/// <para>
/// <b>已发布版本的名称与校验和不得修改</b>：<c>validateMigrationRecord</c> 会逐条比对，
/// 改了就拒绝启动。新增结构变更必须追加新版本。
/// </para>
/// </remarks>
public static class SchemaMigrationCatalog
{
    /// <summary>对应 Go: <c>database.CurrentSchemaVersion</c>。</summary>
    public const long CurrentSchemaVersion = 15;

    /// <summary>PostgreSQL 迁移排他锁 ID。对应 Go: <c>postgresSchemaMigrationLockID</c>。</summary>
    public const long PostgresMigrationLockId = 73123910420260830;

    // 校验和常量，与 Go 完全一致。
    private const string BaselineChecksum = "sha256:open-ai-canvas-schema-v1-20260830";
    private const string AppliedAtIndexChecksum = "sha256:schema-migrations-applied-at-index-v2-20260830";
    private const string AssetTaxonomyChecksum = "sha256:asset-taxonomy-candidate-identity-v3-20260831-r1";
    private const string ResourceUploadKeyChecksum = "sha256:resource-upload-key-v4-20260901";
    private const string PaymentTopupChecksum = "sha256:payment-topup-v5-20260902";
    private const string ResourcePlaybackChecksum = "sha256:resource-playback-v6-20260902";
    private const string AssetLibraryFoldersChecksum = "sha256:asset-library-folders-v6-20260902";
    private const string LogicalModelActiveCodeChecksum = "sha256:logical-model-active-code-v8-20260905";
    private const string ChannelPresentationChecksum = "sha256:channel-presentation-v9-20260908";
    private const string CreationRuntimeChecksum = "sha256:creation-runtime-v10-20260909";
    private const string CloudAgentRuntimeChecksum = "sha256:cloud-agent-runtime-v11-20260912";
    private const string AgentTokenChargeLimitChecksum = "sha256:agent-token-charge-limit-v12-20260913";
    private const string CloudAgentCanvasMutationChecksum = "sha256:cloud-agent-canvas-mutation-v13-20260913";
    private const string CloudAgentRecoveryChecksum = "sha256:cloud-agent-recovery-control-v14";
    private const string AgentProfilesChecksum = "sha256:agent-profiles-v15-20260914";

    /// <summary>标准顺序的迁移计划。</summary>
    public static IReadOnlyList<SchemaMigration> Plan { get; } = BuildPlan();

    private static List<SchemaMigration> BuildPlan() =>
    [
        new SchemaMigration
        {
            Version = 1,
            Name = "baseline_gorm_schema",
            Checksum = BaselineChecksum,
            Apply = Baseline.ApplyAsync,
        },
        new SchemaMigration
        {
            Version = 2,
            Name = "schema_migrations_applied_at_index",
            Checksum = AppliedAtIndexChecksum,
            Apply = ctx => ctx.ExecuteAsync(
                "CREATE INDEX IF NOT EXISTS idx_schema_migrations_applied_at ON schema_migrations (applied_at)"),
        },
        new SchemaMigration
        {
            Version = 3,
            Name = "asset_taxonomy_candidate_identity",
            Checksum = AssetTaxonomyChecksum,
            Apply = Baseline.ApplyAssetTaxonomyAsync,
        },
        new SchemaMigration
        {
            Version = 4,
            Name = "resource_upload_key",
            Checksum = ResourceUploadKeyChecksum,
            Apply = async ctx =>
            {
                if (!await ctx.TableExistsAsync("resources").ConfigureAwait(false))
                {
                    throw new InvalidOperationException("资源表不存在");
                }

                await ctx.AddColumnIfMissingAsync("resources", "upload_key", ctx.ColumnDefinition("resources", "upload_key")).ConfigureAwait(false);
                await ctx.ExecuteAsync("CREATE UNIQUE INDEX IF NOT EXISTS idx_resources_user_upload_key ON resources (user_id, upload_key)").ConfigureAwait(false);
            },
        },
        new SchemaMigration
        {
            Version = 5,
            Name = "payment_topup",
            Checksum = PaymentTopupChecksum,
            Apply = Baseline.ApplyFullSchemaAsync,
        },
        new SchemaMigration
        {
            Version = 6,
            Name = "resource_playback_variant",
            Checksum = ResourcePlaybackChecksum,
            Apply = async ctx =>
            {
                if (!await ctx.TableExistsAsync("resources").ConfigureAwait(false))
                {
                    throw new InvalidOperationException("资源表不存在");
                }

                foreach (string column in new[] { "playback_status", "playback_object_key", "playback_error" })
                {
                    await ctx.AddColumnIfMissingAsync("resources", column, ctx.ColumnDefinition("resources", column)).ConfigureAwait(false);
                }
            },
        },
        new SchemaMigration
        {
            Version = 7,
            Name = "asset_library_folders",
            Checksum = AssetLibraryFoldersChecksum,
            Apply = Baseline.ApplyFullSchemaAsync,
        },
        new SchemaMigration
        {
            Version = 8,
            Name = "logical_model_active_code",
            Checksum = LogicalModelActiveCodeChecksum,
            Apply = async ctx =>
            {
                if (!await ctx.TableExistsAsync("logical_models").ConfigureAwait(false))
                {
                    return;
                }

                await ctx.ExecuteAsync("DROP INDEX IF EXISTS idx_logical_models_code").ConfigureAwait(false);
                await ctx.ExecuteAsync(
                    "CREATE UNIQUE INDEX idx_logical_models_code ON logical_models(code) WHERE archived_at IS NULL").ConfigureAwait(false);
            },
        },
        new SchemaMigration
        {
            Version = 9,
            Name = "channel_presentation",
            Checksum = ChannelPresentationChecksum,
            Apply = async ctx =>
            {
                foreach ((string table, string column) in new[]
                {
                    ("model_channels", "public_alias"),
                    ("model_channels", "sort_order"),
                    ("channel_models", "sort_order"),
                })
                {
                    await ctx.AddColumnIfMissingAsync(table, column, ctx.ColumnDefinition(table, column)).ConfigureAwait(false);
                }
            },
        },
        new SchemaMigration
        {
            Version = 10,
            Name = "creation_runtime",
            Checksum = CreationRuntimeChecksum,
            Apply = Baseline.ApplyFullSchemaAsync,
        },
        new SchemaMigration
        {
            Version = 11,
            Name = "cloud_agent_runtime",
            Checksum = CloudAgentRuntimeChecksum,
            Apply = Baseline.ApplyFullSchemaAsync,
        },
        new SchemaMigration
        {
            Version = 12,
            Name = "agent_token_charge_limit",
            Checksum = AgentTokenChargeLimitChecksum,
            Apply = async ctx =>
            {
                if (!await ctx.TableExistsAsync("billing_orders").ConfigureAwait(false))
                {
                    return;
                }

                await ctx.AddColumnIfMissingAsync(
                    "billing_orders",
                    "charge_limit_microcredits",
                    ctx.ColumnDefinition("billing_orders", "charge_limit_microcredits")).ConfigureAwait(false);
            },
        },
        new SchemaMigration
        {
            Version = 13,
            Name = "cloud_agent_canvas_mutation",
            Checksum = CloudAgentCanvasMutationChecksum,
            Apply = Baseline.ApplyFullSchemaAsync,
        },
        new SchemaMigration
        {
            Version = 14,
            Name = "cloud_agent_recovery_control",
            Checksum = CloudAgentRecoveryChecksum,
            Apply = Baseline.ApplyCloudAgentRecoveryAsync,
        },
        new SchemaMigration
        {
            Version = 15,
            Name = "agent_profiles",
            Checksum = AgentProfilesChecksum,
            Apply = Baseline.ApplyFullSchemaAsync,
        },
    ];

    /// <summary>
    /// 按数据库实际情况选择迁移计划。对应 Go: <c>migrationsForDatabase</c>。
    /// </summary>
    /// <remarks>
    /// 历史发布曾把版本 6 用于 <c>asset_library_folders</c>，之后合并引入了
    /// "6 播放副本、7 素材目录" 的顺序。仅当版本 6 记录的名称与校验和精确匹配历史值时，
    /// 才切换到历史顺序；否则一律使用标准顺序。
    /// </remarks>
    public static IReadOnlyList<SchemaMigration> PlanFor(SchemaMigrationRecord? version6Record)
    {
        if (version6Record is null)
        {
            return Plan;
        }

        if (!string.Equals(version6Record.Name, "asset_library_folders", StringComparison.Ordinal))
        {
            return Plan;
        }

        if (!string.Equals(version6Record.Checksum, AssetLibraryFoldersChecksum, StringComparison.Ordinal))
        {
            return Plan;
        }

        // 历史顺序：版本 6 保留为素材目录，版本 7 改为播放副本。
        List<SchemaMigration> legacy = [];
        foreach (SchemaMigration item in Plan)
        {
            legacy.Add(item.Version switch
            {
                6 => item,
                7 => new SchemaMigration
                {
                    Version = 7,
                    Name = "resource_playback_variant",
                    Checksum = ResourcePlaybackChecksum,
                    Apply = Plan.First(m => m.Version == 6).Apply,
                },
                _ => item,
            });
        }

        return legacy;
    }
}
