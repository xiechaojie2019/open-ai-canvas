#nullable enable
using System.Text.Json.Nodes;
using OpenAICanvas.Application.CloudAgent;
using Xunit;

namespace OpenAICanvas.Tests.CloudAgent;

/// <summary>
/// 「同一来源的多次生成归并到同一个结果节点 + 保留历史」的契约。
/// </summary>
/// <remarks>
/// 产品要求（2026-10-13）：同一个生成节点多次执行任务时，不要每次都新增图片节点和连线，
/// 而是在原节点上展示最新生成的图片，同时保留历史生成结果、支持切换查看。
/// <para>
/// 线上现象：Agent 对同一个来源节点执行 3 次 → 画布上多出 3 个结果节点 + 3 条
/// <c>agent-*</c> 连线。根因是 <c>MediaDocumentAsync</c> 明确拒绝复用「已出图的成品节点」
/// （文案是「目标不是可续用的未提交媒体草稿…不要循环创建替代节点」），
/// 于是模型每次只能换一个 <c>nodeId</c>，服务端就再建一个。
/// </para>
/// <para>
/// 这里同时锁住 helper 行为与<b>调用点接线</b>：helper 写对了但没接到
/// <c>MediaDocumentAsync</c>/<c>CreateMediaNodeAsync</c>/<c>CompleteMediaNodeAsync</c>，
/// 线上行为一点不变。
/// </para>
/// </remarks>
public sealed class CloudAgentMediaAggregationTests
{
    // ------------------------------------------------------------------ 来源 key

    [Fact]
    public void 来源key由媒体大类与归并锚点拼成()
    {
        // 没有参考图：锚点回落到来源节点。
        Assert.Equal("v1|image|-", CloudAgentMediaAggregation.SourceKey(Args()));
        Assert.Equal("v1|image|shot-1", CloudAgentMediaAggregation.SourceKey(Args("image", "shot-1")));
        Assert.Equal("v1|video|-", CloudAgentMediaAggregation.SourceKey(Args("video")));
        // 有参考图：以参考图集合为准，来源节点不再参与（同一张图的任何生成路径都落一起）。
        Assert.Equal("v1|image|a,b", CloudAgentMediaAggregation.SourceKey(Args("image", "shot-1", "b", "a")));
        Assert.Equal("v1|image|a,b", CloudAgentMediaAggregation.SourceKey(Args("image", "other", "b", "a")));
    }

    /// <summary>参考图顺序不同、或重复传同一个节点，必须归并到同一个目标。</summary>
    [Fact]
    public void 来源key与参考图顺序无关且会去重()
    {
        string one = CloudAgentMediaAggregation.SourceKey(Args("image", "", "b", "a", "c"));
        string two = CloudAgentMediaAggregation.SourceKey(Args("image", "", "c", "b", "a"));
        string dup = CloudAgentMediaAggregation.SourceKey(Args("image", "", "a", "c", "b", "a"));

        Assert.Equal(one, two);
        Assert.Equal(one, dup);
        // 空串参考图（模型偶尔会塞空 ID）不参与拼接。
        Assert.Equal(one, CloudAgentMediaAggregation.SourceKey(Args("image", "", "b", "", "a", "c")));
    }

    /// <summary>
    /// 前后端必须算出<b>逐字相同</b>的 key，否则 Agent 生成与手动生成会各自归并到不同的节点上。
    /// </summary>
    [Fact]
    public void 来源key必须与前端实现一致()
    {
        string? path = FrontendHistoryPath();
        Assert.True(path is not null, "找不到前端 canvas-media-history.ts");

        string frontend = File.ReadAllText(path!);
        // 前端表达式本身：anchor = 参考图拼接 / 来源节点，`["v1", mode, anchor].join("|")`
        Assert.Contains("const anchor = refs.length ? refs.join(\",\") : sourceNodeId || \"-\";", frontend);
        Assert.Contains("[\"v1\", mode, anchor].join(\"|\")", frontend);
        // 参考图必须先按字面量排序再拼，否则顺序不同会算成两个来源。
        Assert.Contains("].sort();", frontend);

        // 后端对同一组输入必须给出同一串。注意排序是 Ordinal（≈ JS 默认的 UTF-16 码元序），
        // 所以 "ref-10" 排在 "ref-2" 之前。
        Assert.Equal(
            "v1|image|ref-10,ref-2",
            CloudAgentMediaAggregation.SourceKey(Args("image", "shot-1", "ref-10", "ref-2")));
    }

    /// <summary>
    /// 同一张参考图，不管来自手动生成还是 Agent 生成（来源节点段不同），都必须算同一个来源。
    /// 线上实测：Agent 写 <c>v1|image|-|cat</c>、手动写 <c>v1|image|img-catbg3g|cat</c>，
    /// 于是同一张图被拆成两个节点。
    /// </summary>
    [Fact]
    public void 同一张参考图的不同生成路径归并到同一个来源()
    {
        string agent = CloudAgentMediaAggregation.SourceKey(Args("image", "", "cat"));
        string manual = CloudAgentMediaAggregation.SourceKey(Args("image", "img-catbg3g", "cat"));
        string self = CloudAgentMediaAggregation.SourceKey(Args("image", "image-1791883570602", "cat"));

        Assert.Equal("v1|image|cat", agent);
        Assert.Equal(agent, manual);
        Assert.Equal(agent, self);
    }

    /// <summary>兼容老画布上的四段旧 key：只比「媒体大类 + 锚点」，忽略来源节点段。</summary>
    [Fact]
    public void 来源身份忽略来源节点段并兼容旧格式()
    {
        Assert.Equal("image|cat", CloudAgentMediaAggregation.SourceIdentity("v1|image|-|cat"));
        Assert.Equal("image|cat", CloudAgentMediaAggregation.SourceIdentity("v1|image|img-catbg3g|cat"));
        Assert.Equal("image|cat", CloudAgentMediaAggregation.SourceIdentity("v1|image|cat"));
        // 无参考图的旧格式回落到来源节点；媒体大类必须仍然区分图片与视频。
        Assert.Equal("image|shot-1", CloudAgentMediaAggregation.SourceIdentity("v1|image|shot-1|-"));
        Assert.Equal("video|shot-1", CloudAgentMediaAggregation.SourceIdentity("v1|video|shot-1|-"));
        Assert.NotEqual(
            CloudAgentMediaAggregation.SourceIdentity("v1|image|cat"),
            CloudAgentMediaAggregation.SourceIdentity("v1|video|cat"));
    }

    // ------------------------------------------------------------------ 归并目标解析

    [Fact]
    public void 只复用带同源标记的节点()
    {
        JsonObject doc = Doc(
            Node("plain"),
            Node("other", sourceKey: "v1|video|-|-"));

        Assert.Equal("", CloudAgentMediaAggregation.ResolveTargetNodeID(doc, Args()));
    }

    [Fact]
    public void 同源节点取画布上最后一个命中()
    {
        JsonObject doc = Doc(
            Node("first", sourceKey: "v1|image|-|-"),
            Node("unrelated", sourceKey: "v1|video|-|-"),
            Node("second", sourceKey: "v1|image|-|-"));

        Assert.Equal("second", CloudAgentMediaAggregation.ResolveTargetNodeID(doc, Args()));
    }

    [Fact]
    public void 被锁定的同源节点不作为归并目标()
    {
        JsonObject doc = Doc(
            Node("locked", sourceKey: "v1|image|-|-", locked: true),
            Node("free", sourceKey: "v1|image|-|-"),
            Node("also-locked", sourceKey: "v1|image|-|-", locked: true));

        Assert.Equal("free", CloudAgentMediaAggregation.ResolveTargetNodeID(doc, Args()));
    }

    // ------------------------------------------------------------------ 历史栈

    [Fact]
    public void 压历史把展示中的产物搬进历史并清空顶层()
    {
        JsonObject meta = new()
        {
            ["status"] = "success",
            ["storageKey"] = "resource:r1",
            ["content"] = "/api/resources/r1/file",
            ["naturalWidth"] = 1024,
            ["prompt"] = "画一只猫",
            ["taskId"] = "task-1",
        };

        Assert.True(CloudAgentMediaAggregation.PushCurrentMediaToHistory(meta, "新图"));

        JsonArray history = Assert.IsType<JsonArray>(meta["mediaHistory"]);
        JsonObject entry = Assert.IsType<JsonObject>(history[0]);
        Assert.Equal("resource:r1", (string?)entry["storageKey"]);
        Assert.Equal("/api/resources/r1/file", (string?)entry["content"]);
        Assert.Equal(1024, (int?)entry["naturalWidth"]);
        Assert.Equal("画一只猫", (string?)entry["prompt"]);
        Assert.Equal("task-1", (string?)entry["taskId"]);
        Assert.Equal("新图", (string?)entry["title"]);
        Assert.StartsWith("hist-", (string?)entry["id"]);
        Assert.False(string.IsNullOrEmpty((string?)entry["createdAt"]));

        // 顶层字段必须清干净，否则「当前展示」与历史会指向同一张图。
        foreach (string key in new[] { "storageKey", "content", "naturalWidth" })
        {
            Assert.False(meta.ContainsKey(key), $"顶层字段 {key} 必须被移走");
        }
        // 非产物字段保留。
        Assert.Equal("success", (string?)meta["status"]);
    }

    [Fact]
    public void 顶层没有产物时压历史是空操作()
    {
        JsonObject meta = new() { ["status"] = "idle", ["prompt"] = "画一只猫" };

        Assert.False(CloudAgentMediaAggregation.PushCurrentMediaToHistory(meta, "新图"));
        Assert.False(meta.ContainsKey("mediaHistory"));
    }

    /// <summary>回写重试或「建草稿 + 提交」两次写入都不得把同一张图重复入栈。</summary>
    [Fact]
    public void 同一产物重复入栈会被跳过()
    {
        JsonObject meta = new() { ["storageKey"] = "resource:r1", ["content"] = "/a" };
        Assert.True(CloudAgentMediaAggregation.PushCurrentMediaToHistory(meta, "新图"));
        // 第二次调用时顶层已被清空 ⇒ 直接空操作。
        Assert.False(CloudAgentMediaAggregation.PushCurrentMediaToHistory(meta, "新图"));
        Assert.Single(Assert.IsType<JsonArray>(meta["mediaHistory"]));

        // 幂等保护还要能挡住「同 storageKey 的第二次提交」，图层面的重复。
        JsonObject again = new() { ["storageKey"] = "resource:r1", ["content"] = "/a" };
        JsonArray seeded = Assert.IsType<JsonArray>(meta["mediaHistory"]);
        again["mediaHistory"] = new JsonArray(seeded.Select(n => (JsonNode?)n!.DeepClone()).ToArray());
        Assert.True(CloudAgentMediaAggregation.PushCurrentMediaToHistory(again, "新图"));
        Assert.Single(Assert.IsType<JsonArray>(again["mediaHistory"]));
    }

    [Fact]
    public void 历史超过上限时丢弃最旧的一条()
    {
        JsonObject meta = new();
        for (int index = 0; index < CloudAgentMediaAggregation.HistoryLimit + 5; index++)
        {
            meta["storageKey"] = $"resource:r{index}";
            meta["content"] = $"/{index}";
            Assert.True(CloudAgentMediaAggregation.PushCurrentMediaToHistory(meta, "新图"));
        }

        JsonArray history = Assert.IsType<JsonArray>(meta["mediaHistory"]);
        Assert.Equal(CloudAgentMediaAggregation.HistoryLimit, history.Count);
        // 最新在前：最后压进去的那张必须在最前面。
        Assert.Equal(
            $"resource:r{CloudAgentMediaAggregation.HistoryLimit + 4}",
            Assert.IsType<JsonObject>(history[0])["storageKey"]?.GetValue<string>());
    }

    // ------------------------------------------------------------------ 重写保留 + 任务账本

    [Fact]
    public void 重写节点元数据时保留产物与历史()
    {
        JsonObject previous = new()
        {
            ["storageKey"] = "resource:r1",
            ["content"] = "/api/resources/r1/file",
            ["mediaHistory"] = new JsonArray(new JsonObject { ["id"] = "hist-1", ["storageKey"] = "resource:r0" }),
            ["generationSourceKey"] = "v1|image|-|-",
            ["mediaTaskIds"] = new JsonArray("task-old"),
        };
        // 新草稿元数据是整段新建的，默认没有任何产物字段。
        JsonObject next = new() { ["status"] = "idle", ["prompt"] = "画一只猫" };

        CloudAgentMediaAggregation.PreserveAcrossRewrite(previous, next, "task-new");

        Assert.Equal("resource:r1", (string?)next["storageKey"]);
        Assert.Equal("/api/resources/r1/file", (string?)next["content"]);
        Assert.Equal("v1|image|-|-", (string?)next["generationSourceKey"]);
        Assert.Single(Assert.IsType<JsonArray>(next["mediaHistory"]));
        Assert.Equal(
            new[] { "task-old", "task-new" },
            Assert.IsType<JsonArray>(next["mediaTaskIds"]).Select(n => (string?)n).ToArray());
    }

    [Fact]
    public void 任务账本去重追加并截断到上限()
    {
        JsonObject previous = new()
        {
            ["mediaTaskIds"] = new JsonArray(
                Enumerable.Range(0, CloudAgentMediaAggregation.TaskIDLimit)
                    .Select(i => (JsonNode?)$"task-{i}").ToArray()),
        };
        JsonObject next = new();

        CloudAgentMediaAggregation.PreserveAcrossRewrite(previous, next, "task-new");

        string?[] ids = Assert.IsType<JsonArray>(next["mediaTaskIds"])
            .Select(n => (string?)n).ToArray();
        Assert.Equal(CloudAgentMediaAggregation.TaskIDLimit, ids.Length);
        Assert.Equal("task-new", ids[^1]);
        // 最旧的被挤掉。
        Assert.DoesNotContain("task-0", ids);

        // 重复追加同一个任务不得把账本撑大。
        JsonObject repeat = new();
        CloudAgentMediaAggregation.PreserveAcrossRewrite(next, repeat, "task-new");
        Assert.Equal(
            CloudAgentMediaAggregation.TaskIDLimit,
            Assert.IsType<JsonArray>(repeat["mediaTaskIds"]).Count);
    }

    [Fact]
    public void 完成回写按任务账本匹配节点()
    {
        JsonObject meta = new()
        {
            ["taskId"] = "task-new",
            ["mediaTaskIds"] = new JsonArray("task-1", "task-2", "task-new"),
        };

        // 同一节点先后承接多个任务：账本里的旧任务也必须能匹配回来。
        Assert.True(CloudAgentMediaAggregation.OwnsTask(meta, "task-1"));
        Assert.True(CloudAgentMediaAggregation.OwnsTask(meta, "task-new"));
        Assert.False(CloudAgentMediaAggregation.OwnsTask(meta, "task-other"));
        Assert.False(CloudAgentMediaAggregation.OwnsTask(meta, ""));

        // 只有单个 taskId 的老节点（未归并过的画布）行为不变。
        Assert.True(CloudAgentMediaAggregation.OwnsTask(new JsonObject { ["taskId"] = "task-old" }, "task-old"));
    }

    [Fact]
    public void 当前产物判定覆盖图片与视频两种形态()
    {
        Assert.True(CloudAgentMediaAggregation.HasCurrentMedia(new JsonObject { ["storageKey"] = "resource:r1" }));
        Assert.True(CloudAgentMediaAggregation.HasCurrentMedia(new JsonObject { ["content"] = "/a" }));
        Assert.True(CloudAgentMediaAggregation.HasCurrentMedia(new JsonObject
        {
            ["videoPreview"] = new JsonObject { ["storageKey"] = "resource:r2" },
        }));
        Assert.False(CloudAgentMediaAggregation.HasCurrentMedia(new JsonObject { ["status"] = "idle" }));
        Assert.False(CloudAgentMediaAggregation.HasCurrentMedia(null));
    }

    // ------------------------------------------------------------------ 接线（源码级）

    /// <summary>
    /// 归并必须真的接到四个调用点；少任何一处，线上就还是「每次生成新增节点 + 连线」。
    /// </summary>
    [Fact]
    public void Agent多张不归并每张独立节点()
    {
        string media = MediaServiceSource();

        // ① 准入阶段不再解析/复用同源节点：Agent 不做归并（归并只保留在手动生成一侧）。
        Assert.DoesNotContain("ResolveTargetNodeID(doc, args)", media);
        Assert.Contains("if (existing is not null)", media);

        // ② 展开：一次出多张时每张派生自己的节点 ID。
        Assert.Contains("List<string> nodeIDs = BatchNodeIDs(args);", media);

        // ③ 建节点**不再**打来源标记：打上之后手动生成会复用到 Agent 建的节点上
        //（线上表现：「在手动节点上发任务，结果跑到 Agent 图片上」）。产物/历史/账本照旧保留。
        Assert.DoesNotContain("meta[\"generationSourceKey\"]", media);
        Assert.Contains("CloudAgentMediaAggregation.PreserveAcrossRewrite(previous, meta, task?.ID ?? \"\");", media);

        // ④ 完成回写仍按任务账本匹配。
        Assert.Contains("if (!CloudAgentMediaAggregation.OwnsTask(meta, task.ID))", media);
        Assert.Contains("CloudAgentMediaAggregation.PushCurrentMediaToHistory(", media);
    }

    /// <summary>
    /// 一次出多张时每张各自成节点（2026-10-14 起不再共用节点），标题必须带「 1/2/3」序号以区分。
    /// </summary>
    [Fact]
    public void 多张时标题带序号()
    {
        Assert.Contains("CloudAgentContracts.TruncateRunes(title, 240 - suffix.Length) + suffix", MediaServiceSource());
    }

    // ------------------------------------------------------------------ 夹具

    private static CloudAgentMediaArgs Args(
        string mode = "image", string source = "", params string[] refs) => new()
        {
            Mode = mode,
            SourceNodeID = source,
            NodeID = "image-new",
            ReferenceNodeIDs = [.. refs],
        };

    private static JsonObject Doc(params JsonObject[] nodes) => new()
    {
        ["nodes"] = new JsonArray(nodes.Select(node => (JsonNode?)node).ToArray()),
        ["connections"] = new JsonArray(),
    };

    private static JsonObject Node(string id, string? sourceKey = null, bool locked = false)
    {
        JsonObject metadata = new();
        if (sourceKey is not null)
        {
            metadata["generationSourceKey"] = sourceKey;
        }
        if (locked)
        {
            metadata["locked"] = true;
        }
        return new JsonObject
        {
            ["id"] = id,
            ["type"] = "image",
            ["metadata"] = metadata,
        };
    }

    private static string MediaServiceSource() => File.ReadAllText(Path.Combine(
        RepoRoot(), "src", "OpenAICanvas.Application", "CloudAgent", "CloudAgentMediaService.cs"));

    private static string? FrontendHistoryPath()
    {
        string path = Path.GetFullPath(Path.Combine(
            RepoRoot(), "..", "web", "src", "lib", "canvas", "canvas-media-history.ts"));
        return File.Exists(path) ? path : null;
    }

    private static string RepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OpenAICanvas.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("找不到仓库根目录");
    }
}
