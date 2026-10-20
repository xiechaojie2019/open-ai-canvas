using OpenAICanvas.Persistence;
using OpenAICanvas.Persistence.Schema;
using Xunit;

namespace OpenAICanvas.Tests.Persistence;

/// <summary>
/// 号段撞车回归：Go 版在 .NET 的 <c>camel_case_identifiers</c> 之后又追加了 35~37，
/// 导致线上库版本高于 .NET 支持的上界而拒绝启动。
/// </summary>
public class SchemaVersionTakeoverTests
{
    [Fact]
    public void 标准计划复用_go_35_到_37_的名称与校验和()
    {
        SchemaMigration[] plan = SchemaMigrationCatalog.Plan.ToArray();

        SchemaMigration v35 = plan.Single(m => m.Version == 35);
        SchemaMigration v36 = plan.Single(m => m.Version == 36);
        SchemaMigration v37 = plan.Single(m => m.Version == 37);

        Assert.Equal("auth_notifications", v35.Name);
        Assert.Equal("sha256:auth-notifications-v35-20260924", v35.Checksum);
        Assert.Equal("cloud_agent_gemini_cache", v36.Name);
        Assert.Equal("sha256:cloud-agent-gemini-cache-v36-20260924", v36.Checksum);
        Assert.Equal("cloud_agent_gemini_cache_identity", v37.Name);
        Assert.Equal("sha256:cloud-agent-gemini-cache-identity-v37-20260925", v37.Checksum);
    }

    [Fact]
    public void 改名迁移让到_go_号段之后()
    {
        Assert.Equal(38, SchemaMigrationCatalog.CurrentSchemaVersion);

        SchemaMigration camel = SchemaMigrationCatalog.Plan.Single(m => m.Name == "camel_case_identifiers");
        Assert.Equal(38, camel.Version);

        // 版本号必须连续，否则 ValidateAllRecordsAsync 会判「缺少迁移记录」。
        long[] versions = SchemaMigrationCatalog.Plan.Select(m => m.Version).OrderBy(v => v).ToArray();
        Assert.Equal(Enumerable.Range(1, 38).Select(v => (long)v), versions);
    }

    [Fact]
    public void 已有_dotnet_老_v35_记录的库走顺延计划()
    {
        // 早期 .NET 独占 35 号时的记录。
        SchemaMigrationRecord legacy35 = new()
        {
            Version = 35,
            Name = "camel_case_identifiers",
            Checksum = "sha256:camel-case-identifiers-v35-20260929",
        };

        IReadOnlyList<SchemaMigration> plan = SchemaMigrationCatalog.PlanFor(version6Record: null, legacy35);

        // 35 号保持不变，Go 的三条顺延到 36/37/38。
        Assert.Equal("camel_case_identifiers", plan.Single(m => m.Version == 35).Name);
        Assert.Equal("sha256:camel-case-identifiers-v35-20260929", plan.Single(m => m.Version == 35).Checksum);
        Assert.Equal("auth_notifications", plan.Single(m => m.Version == 36).Name);
        Assert.Equal("cloud_agent_gemini_cache", plan.Single(m => m.Version == 37).Name);
        Assert.Equal("cloud_agent_gemini_cache_identity", plan.Single(m => m.Version == 38).Name);

        // 38 号不再是改名迁移，避免对已改名库重复执行。
        Assert.Equal("cloud_agent_gemini_cache_identity", plan.Single(m => m.Version == 38).Name);
    }

    [Fact]
    public void go_库的标准计划不受_dotnet_老记录影响()
    {
        SchemaMigrationRecord go35 = new()
        {
            Version = 35,
            Name = "auth_notifications",
            Checksum = "sha256:auth-notifications-v35-20260924",
        };

        IReadOnlyList<SchemaMigration> plan = SchemaMigrationCatalog.PlanFor(version6Record: null, go35);

        Assert.Equal(SchemaMigrationCatalog.Plan, plan);
        Assert.Equal("auth_notifications", plan.Single(m => m.Version == 35).Name);
        Assert.Equal("camel_case_identifiers", plan.Single(m => m.Version == 38).Name);
    }

    [Fact]
    public void 版本6历史特例与版本35顺延可以叠加()
    {
        SchemaMigrationRecord legacy6 = new()
        {
            Version = 6,
            Name = "asset_library_folders",
            Checksum = "sha256:asset-library-folders-v6-20260902",
        };
        SchemaMigrationRecord legacy35 = new()
        {
            Version = 35,
            Name = "camel_case_identifiers",
            Checksum = "sha256:camel-case-identifiers-v35-20260929",
        };

        IReadOnlyList<SchemaMigration> plan = SchemaMigrationCatalog.PlanFor(legacy6, legacy35);

        // 版本 6 历史特例与版本 35 顺延互不影响，可叠加（6/7 号的历史怪癖不在本用例关注范围）。
        Assert.Equal("camel_case_identifiers", plan.Single(m => m.Version == 35).Name);
        Assert.Equal("auth_notifications", plan.Single(m => m.Version == 36).Name);
        Assert.Equal("cloud_agent_gemini_cache", plan.Single(m => m.Version == 37).Name);
        Assert.Equal("cloud_agent_gemini_cache_identity", plan.Single(m => m.Version == 38).Name);
    }
}