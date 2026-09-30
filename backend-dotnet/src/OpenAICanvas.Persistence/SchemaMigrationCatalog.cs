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
    /// <remarks>
    /// 35 是 .NET 侧独有的 <c>camel_case_identifiers</c>（库内标识符 snake_case → camelCase），
    /// 排在 Go 的 v34 之后，因此不占用 Go 的号段。
    /// </remarks>
    public const long CurrentSchemaVersion = 35;

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
    private const string AgentLessonsChecksum = "sha256:agent-lessons-v16-20260917";
    private const string AgentLessonsOwnerIndexChecksum = "sha256:agent-lessons-owner-index-v17-20260917";
    private const string AgentMemorySettingsChecksum = "sha256:agent-memory-settings-v18-20260917";
    private const string PaymentPluginVersionChecksum = "sha256:payment-plugin-version-v19-20260917";
    private const string BannerAnnouncementsChecksum = "sha256:banner-announcements-v20-20260917";
    private const string BannerTitleRunsChecksum = "sha256:banner-announcement-title-runs-v21-20260917";
    private const string BannerNoticeTypeChecksum = "sha256:banner-announcement-notice-type-v22-20260917";
    private const string CanvasRevisionHistoryChecksum = "sha256:canvas-revision-history-v23-20260918";
    private const string ChannelModelLabelChecksum = "sha256:channel-model-label-v24";
    private const string VideoTokenFormulaSnapshotChecksum = "sha256:video-token-formula-snapshot-v25";
    private const string ChannelModelDescriptionChecksum = "sha256:channel-model-description-v26";
    private const string ChannelCreditCostChecksum = "sha256:channel-credit-cost-v27";
    private const string AgentExecutionJournalChecksum = "sha256:agent-execution-journal-v28";
    private const string AgentResourceLeasesChecksum = "sha256:agent-resource-leases-v29-20260919";
    private const string BuiltinToolsChecksum = "sha256:builtin-tools-v30";
    private const string ToolFavoritesChecksum = "sha256:tool-favorites-v31";
    private const string ChannelModelTagsChecksum = "sha256:channel-model-tags-v32";
    private const string OAuthStateAcceptedTermsChecksum = "sha256:oauth-state-accepted-terms-v33";
    private const string TaskMediaRecoveryChecksum = "sha256:task-media-recovery-v34";
    private const string CamelCaseIdentifiersChecksum = "sha256:camel-case-identifiers-v35-20260929";

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
        // —— v16–v34 对齐 Go 的 schemaMigrations（2026-09-28 移植）。
        // 此前 .NET 曾把 v16 槽位用于自建的 canvas_project_revision 临时条目，
        // 画布同步 CAS 的 revision 列现由 Go 同名版本 v23 canvas_revision_history 负责。
        new SchemaMigration
        {
            Version = 16,
            Name = "agent_lessons",
            Checksum = AgentLessonsChecksum,
            Apply = Baseline.ApplyFullSchemaAsync,
        },
        new SchemaMigration
        {
            Version = 17,
            Name = "agent_lessons_owner_index",
            Checksum = AgentLessonsOwnerIndexChecksum,
            Apply = Baseline.ApplyFullSchemaAsync,
        },
        new SchemaMigration
        {
            Version = 18,
            Name = "agent_memory_settings",
            Checksum = AgentMemorySettingsChecksum,
            Apply = Baseline.ApplyFullSchemaAsync,
        },
        new SchemaMigration
        {
            Version = 19,
            Name = "payment_plugin_version",
            Checksum = PaymentPluginVersionChecksum,
            Apply = async ctx =>
            {
                foreach (string table in new[] { "payment_provider_configs", "payment_orders" })
                {
                    if (!await ctx.TableExistsAsync(table).ConfigureAwait(false))
                    {
                        continue;
                    }

                    await ctx.AddColumnIfMissingAsync(
                        table, "plugin_version", ctx.ColumnDefinition(table, "plugin_version")).ConfigureAwait(false);
                }
            },
        },
        new SchemaMigration
        {
            Version = 20,
            Name = "banner_announcements",
            Checksum = BannerAnnouncementsChecksum,
            Apply = Baseline.ApplyFullSchemaAsync,
        },
        new SchemaMigration
        {
            Version = 21,
            Name = "banner_announcement_title_runs",
            Checksum = BannerTitleRunsChecksum,
            Apply = async ctx =>
                await ctx.AddColumnIfMissingAsync(
                    "banner_announcements", "title_runs", ctx.ColumnDefinition("banner_announcements", "title_runs")).ConfigureAwait(false),
        },
        new SchemaMigration
        {
            Version = 22,
            Name = "banner_announcement_notice_type",
            Checksum = BannerNoticeTypeChecksum,
            Apply = async ctx =>
                await ctx.AddColumnIfMissingAsync(
                    "banner_announcements", "notice_type", ctx.ColumnDefinition("banner_announcements", "notice_type")).ConfigureAwait(false),
        },
        new SchemaMigration
        {
            Version = 23,
            Name = "canvas_revision_history",
            Checksum = CanvasRevisionHistoryChecksum,
            Apply = async ctx =>
            {
                await Baseline.ApplyFullSchemaAsync(ctx).ConfigureAwait(false);
                await ctx.AddColumnIfMissingAsync(
                    "canvas_projects", "revision", ctx.ColumnDefinition("canvas_projects", "revision")).ConfigureAwait(false);
            },
        },
        new SchemaMigration
        {
            Version = 24,
            Name = "channel_model_label",
            Checksum = ChannelModelLabelChecksum,
            Apply = async ctx =>
                await ctx.AddColumnIfMissingAsync(
                    "channel_models", "channel_label", ctx.ColumnDefinition("channel_models", "channel_label")).ConfigureAwait(false),
        },
        new SchemaMigration
        {
            Version = 25,
            Name = "video_token_formula_snapshot",
            Checksum = VideoTokenFormulaSnapshotChecksum,
            Apply = async ctx =>
            {
                await ctx.AddColumnIfMissingAsync(
                    "billing_orders", "video_formula_tokens", ctx.ColumnDefinition("billing_orders", "video_formula_tokens")).ConfigureAwait(false);
                await ctx.AddColumnIfMissingAsync(
                    "billing_orders", "usage_source", ctx.ColumnDefinition("billing_orders", "usage_source")).ConfigureAwait(false);
            },
        },
        new SchemaMigration
        {
            Version = 26,
            Name = "channel_model_description",
            Checksum = ChannelModelDescriptionChecksum,
            Apply = async ctx =>
                await ctx.AddColumnIfMissingAsync(
                    "channel_models", "description", ctx.ColumnDefinition("channel_models", "description")).ConfigureAwait(false),
        },
        new SchemaMigration
        {
            Version = 27,
            Name = "channel_credit_cost",
            Checksum = ChannelCreditCostChecksum,
            Apply = async ctx =>
            {
                foreach (string table in new[] { "channel_model_price_tiers", "billing_orders" })
                {
                    await AddColumnsIfMissingAsync(
                        ctx, table,
                        "cost_configured",
                        "cost_unit_price_microcredits",
                        "cost_input_token_price_microcredits",
                        "cost_output_token_price_microcredits",
                        "cost_cached_token_price_microcredits").ConfigureAwait(false);
                }

                await AddColumnsIfMissingAsync(
                    ctx, "billing_orders", "cost_billing_mode", "cost_quantity", "cost_video_formula_tokens").ConfigureAwait(false);
            },
        },
        // v28 的 AutoMigrate(CloudAgentExecution/EventRecord/MessageRecord/Task/BillingOrder)：
        // 新表由完整建表脚本创建，已有表的新列在此逐列补齐。
        new SchemaMigration
        {
            Version = 28,
            Name = "agent_execution_journal",
            Checksum = AgentExecutionJournalChecksum,
            Apply = async ctx =>
            {
                await Baseline.ApplyFullSchemaAsync(ctx).ConfigureAwait(false);
                await AddColumnsIfMissingAsync(
                    ctx, "cloud_agent_executions",
                    "checkpoint_version", "conversation_id", "parent_id", "title", "event_count", "message_count").ConfigureAwait(false);
                await AddColumnsIfMissingAsync(
                    ctx, "tasks",
                    "agent_run_id", "generation_id", "approval_id", "authorized_charge_microcredits",
                    "execution_diagnostic_json", "cancellation_source", "cancellation_actor_id", "cancellation_requested_at").ConfigureAwait(false);
                await AddColumnsIfMissingAsync(ctx, "billing_orders", "charge_limit_set").ConfigureAwait(false);
            },
        },
        new SchemaMigration
        {
            Version = 29,
            Name = "agent_resource_leases",
            Checksum = AgentResourceLeasesChecksum,
            Apply = Baseline.ApplyFullSchemaAsync,
        },
        new SchemaMigration
        {
            Version = 30,
            Name = "builtin_tools",
            Checksum = BuiltinToolsChecksum,
            Apply = Baseline.ApplyFullSchemaAsync,
        },
        new SchemaMigration
        {
            Version = 31,
            Name = "tool_favorites",
            Checksum = ToolFavoritesChecksum,
            Apply = Baseline.ApplyFullSchemaAsync,
        },
        new SchemaMigration
        {
            Version = 32,
            Name = "channel_model_tags",
            Checksum = ChannelModelTagsChecksum,
            Apply = async ctx =>
                await ctx.AddColumnIfMissingAsync(
                    "channel_models", "tags", ctx.ColumnDefinition("channel_models", "tags")).ConfigureAwait(false),
        },
        new SchemaMigration
        {
            Version = 33,
            Name = "oauth_state_accepted_terms",
            Checksum = OAuthStateAcceptedTermsChecksum,
            Apply = async ctx =>
                await ctx.AddColumnIfMissingAsync(
                    "o_auth_states", "accepted_terms", ctx.ColumnDefinition("o_auth_states", "accepted_terms")).ConfigureAwait(false),
        },
        new SchemaMigration
        {
            Version = 34,
            Name = "task_media_recovery",
            Checksum = TaskMediaRecoveryChecksum,
            Apply = async ctx =>
            {
                await AddColumnsIfMissingAsync(ctx, "tasks", "media_recovery_json", "media_stage").ConfigureAwait(false);
            },
        },
        new SchemaMigration
        {
            Version = 35,
            Name = "camel_case_identifiers",
            Checksum = CamelCaseIdentifiersChecksum,
            Apply = Baseline.ApplyCamelCaseIdentifiersAsync,
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

    /// <summary>按完整建表脚本里的定义逐列补齐。对应 Go 的 <c>Migrator().AddColumn()</c> 循环。</summary>
    private static async Task AddColumnsIfMissingAsync(SchemaMigrationContext ctx, string table, params string[] columns)
    {
        foreach (string column in columns)
        {
            await ctx.AddColumnIfMissingAsync(table, column, ctx.ColumnDefinition(table, column)).ConfigureAwait(false);
        }
    }
}
