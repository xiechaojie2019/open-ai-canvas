#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Serialization;
using OpenAICanvas.Persistence.Repositories;

namespace OpenAICanvas.Application.CloudAgent;

/// <summary>单个技能卡读取计数。对应 Go: <c>app.CloudAgentSkillCardUsage</c>。</summary>
public sealed class CloudAgentSkillCardUsageDto
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("count")]
    public int Count { get; set; }
}

/// <summary>单个技能的采用统计。对应 Go: <c>app.CloudAgentSkillUsageEntry</c>（struct 字段序）。</summary>
public sealed class CloudAgentSkillUsageEntryDto
{
    [JsonPropertyName("skillId")]
    public string SkillID { get; set; } = "";

    [JsonPropertyName("skillName")]
    [Domain.Serialization.GoOmitEmpty]
    public string SkillName { get; set; } = "";

    /// <summary>加载了该技能的运行数。</summary>
    [JsonPropertyName("runsEnabled")]
    public int RunsEnabled { get; set; }

    /// <summary>该技能作为命中结果返回的轮次。</summary>
    [JsonPropertyName("searchHits")]
    public int SearchHits { get; set; }

    [JsonPropertyName("readCalls")]
    public int ReadCalls { get; set; }

    /// <summary>被拒绝的读取（坏路径、错技能）单独计数。</summary>
    [JsonPropertyName("readFailures")]
    public int ReadFailures { get; set; }

    /// <summary>SKILL.md/目录列举次数；是成本信号。</summary>
    [JsonPropertyName("entryReads")]
    public int EntryReads { get; set; }

    [JsonPropertyName("cardReads")]
    public int CardReads { get; set; }

    [JsonPropertyName("topCards")]
    [Domain.Serialization.GoOmitEmpty]
    public List<CloudAgentSkillCardUsageDto>? TopCards { get; set; }
}

/// <summary>技能采用汇总。对应 Go: <c>app.CloudAgentSkillUsage</c>（struct 字段序）。</summary>
public sealed class CloudAgentSkillUsageDto
{
    [JsonPropertyName("scannedRuns")]
    public int ScannedRuns { get; set; }

    /// <summary>检索轮次（不归属到单个技能）。</summary>
    [JsonPropertyName("searchCalls")]
    public int SearchCalls { get; set; }

    [JsonPropertyName("skills")]
    public List<CloudAgentSkillUsageEntryDto> Skills { get; set; } = [];
}

/// <summary>
/// 技能采用遥测：全部 Agent 工具调用已落在执行日志里，归因不需要新表和新写路径，
/// 只读回运行已记录的回执。回答技能生态无法回答的问题：技能装上之后，有没有被用到。
/// 对应 Go: <c>app/cloud_agent_skill_usage.go</c>。
/// </summary>
public sealed class CloudAgentSkillUsageService
{
    /// <summary>运行窗口保持聚合有界；更老的运行留在 journal 不重扫。对应 Go: <c>cloudAgentSkillUsageRunWindow</c>。</summary>
    private const int RunWindow = 200;

    /// <summary>卡路径无上限，只回最热的几张。对应 Go: <c>cloudAgentSkillUsageTopCards</c>。</summary>
    private const int TopCards = 5;

    private const string SkillEntryPath = "SKILL.md";

    private readonly Repository _repository;

    public CloudAgentSkillUsageService(Repository repository) => _repository = repository;

    /// <summary>汇总调用者的技能采用。对应 Go: <c>CloudAgentSkillUsage</c>。</summary>
    public async Task<CloudAgentSkillUsageDto> UsageAsync(
        string userID, CancellationToken cancellationToken = default)
    {
        List<CloudAgentEventRecord> records = await _repository.RecentCloudAgentEventsForUserAsync(
            userID, RunWindow, cancellationToken).ConfigureAwait(false);
        List<CloudAgentEventDto> events = new(records.Count);
        foreach (CloudAgentEventRecord record in records)
        {
            // 损坏回执使聚合不完整；不把损坏当作有效用量上报。
            CloudAgentEventDto? agentEvent = JsonSerializer.Deserialize<CloudAgentEventDto>(
                record.EventJSON, GoJson.ReadOptions);
            if (agentEvent is null)
            {
                throw new InvalidOperationException("decode Agent skill usage receipt: null record");
            }
            events.Add(agentEvent);
        }
        return FromEvents(events);
    }

    /// <summary>纯聚合（无数据库）。对应 Go: <c>cloudAgentSkillUsageFromEvents</c>。</summary>
    public static CloudAgentSkillUsageDto FromEvents(IReadOnlyList<CloudAgentEventDto> events)
    {
        CloudAgentSkillUsageDto usage = new() { Skills = [] };
        Dictionary<string, CloudAgentSkillUsageEntryDto> entries = new(StringComparer.Ordinal);
        Dictionary<string, Dictionary<string, int>> cardCounts = new(StringComparer.Ordinal);
        Dictionary<string, bool> scannedRuns = new(StringComparer.Ordinal);

        CloudAgentSkillUsageEntryDto Entry(string skillID)
        {
            if (entries.TryGetValue(skillID, out CloudAgentSkillUsageEntryDto? existing))
            {
                return existing;
            }
            CloudAgentSkillUsageEntryDto created = new() { SkillID = skillID };
            entries[skillID] = created;
            return created;
        }

        foreach (CloudAgentEventDto agentEvent in events)
        {
            if (agentEvent.Type is not ("tool_completed" or "tool_failed"))
            {
                continue;
            }
            string name = PayloadString(agentEvent.Payload, "toolName");
            switch (name)
            {
                case "skills_load":
                    scannedRuns[agentEvent.RunID] = true;
                    foreach (string id in StringSlice(agentEvent.Payload, "skillIds"))
                    {
                        Entry(id).RunsEnabled++;
                    }
                    break;
                case "skill_search":
                    usage.SearchCalls++;
                    if (agentEvent.Payload.TryGetValue("result", out JsonElement result)
                        && result.ValueKind == JsonValueKind.Object
                        && result.TryGetProperty("matches", out JsonElement matches)
                        && matches.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement match in matches.EnumerateArray())
                        {
                            if (match.ValueKind != JsonValueKind.Object
                                || !match.TryGetProperty("skillId", out JsonElement skillIDElement)
                                || skillIDElement.ValueKind != JsonValueKind.String)
                            {
                                continue;
                            }
                            string id = skillIDElement.GetString()?.Trim() ?? "";
                            if (id.Length == 0)
                            {
                                continue;
                            }
                            Entry(id).SearchHits++;
                        }
                    }
                    break;
                case "skill_read_file":
                    string skillIDRead = PayloadString(agentEvent.Payload, "skillId");
                    if (skillIDRead.Trim().Length == 0)
                    {
                        continue;
                    }
                    CloudAgentSkillUsageEntryDto target = Entry(skillIDRead);
                    if (agentEvent.Type == "tool_failed")
                    {
                        target.ReadFailures++;
                        continue;
                    }
                    target.ReadCalls++;
                    string label = PayloadString(agentEvent.Payload, "skillName");
                    if (label.Length > 0)
                    {
                        target.SkillName = label;
                    }
                    string path = PayloadString(agentEvent.Payload, "path");
                    if (path.Length == 0 || path == SkillEntryPath)
                    {
                        target.EntryReads++;
                        continue;
                    }
                    target.CardReads++;
                    if (!cardCounts.TryGetValue(skillIDRead, out Dictionary<string, int>? counts))
                    {
                        counts = new Dictionary<string, int>(StringComparer.Ordinal);
                        cardCounts[skillIDRead] = counts;
                    }
                    counts[path] = counts.GetValueOrDefault(path) + 1;
                    break;
            }
        }
        usage.ScannedRuns = scannedRuns.Count;

        foreach ((string id, CloudAgentSkillUsageEntryDto target) in entries)
        {
            if (!cardCounts.TryGetValue(id, out Dictionary<string, int>? counts) || counts.Count == 0)
            {
                continue;
            }
            List<CloudAgentSkillCardUsageDto> cards =
            [
                .. counts.Select(pair => new CloudAgentSkillCardUsageDto { Path = pair.Key, Count = pair.Value })
                    .OrderByDescending(card => card.Count)
                    .ThenBy(card => card.Path, StringComparer.Ordinal),
            ];
            if (cards.Count > TopCards)
            {
                cards = cards[..TopCards];
            }
            target.TopCards = cards;
        }

        usage.Skills = [.. entries.Values.OrderBy(entry => entry.SkillID, StringComparer.Ordinal)];
        return usage;
    }

    private static string PayloadString(Dictionary<string, JsonElement> payload, string key) =>
        payload.TryGetValue(key, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? ""
            : "";

    private static List<string> StringSlice(Dictionary<string, JsonElement> payload, string key)
    {
        List<string> result = [];
        if (payload.TryGetValue(key, out JsonElement value) && value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    string text = item.GetString()?.Trim() ?? "";
                    if (text.Length > 0)
                    {
                        result.Add(text);
                    }
                }
            }
        }
        return result;
    }
}
