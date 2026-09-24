#nullable enable
using System.Security.Cryptography;
using System.Text;

namespace OpenAICanvas.Prompts;

/// <summary>Agent 策略文档。对应 Go: <c>prompts.Policy</c>。</summary>
public sealed record AgentPolicy(string Id, int Version, string Text, string Hash);

/// <summary>
/// 加载并解析嵌入的 Agent 系统/媒体策略文档。对应 Go: <c>prompts.LoadAgentPolicies</c>。
/// 哈希输入为 <c>id:{id}\nversion:{v}\n{text}</c> 的 SHA256，与 Go 逐字节一致。
/// </summary>
public static class AgentPolicyDocuments
{
    public static async Task<(AgentPolicy System, AgentPolicy Media)> LoadAgentPoliciesAsync(
        CancellationToken cancellationToken = default)
    {
        AgentPolicy system = await LoadPolicyAsync("OpenAICanvas.Prompts.AgentPolicies.agent-system-policy.md", cancellationToken)
            .ConfigureAwait(false);
        AgentPolicy media = await LoadPolicyAsync("OpenAICanvas.Prompts.AgentPolicies.agent-media-policy.md", cancellationToken)
            .ConfigureAwait(false);
        if (system.Id != "cloud-agent-system" || media.Id != "cloud-agent-media")
        {
            throw new InvalidOperationException("agent policy identities are invalid");
        }
        return (system, media);
    }

    private static async Task<AgentPolicy> LoadPolicyAsync(string resourceName, CancellationToken cancellationToken)
    {
        System.Reflection.Assembly assembly = typeof(AgentPolicyDocuments).Assembly;
        await using System.IO.Stream stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"read agent policy {resourceName}: resource missing");
        using StreamReader reader = new(stream, Encoding.UTF8);
        string raw = (await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false)).Trim();
        (string id, int version, string text) = ParsePolicyDocument(raw);
        string normalized = $"id:{id}\nversion:{version}\n{text}";
        byte[] sum = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return new AgentPolicy(id, version, text, Convert.ToHexString(sum).ToLowerInvariant());
    }

    private static (string Id, int Version, string Text) ParsePolicyDocument(string raw)
    {
        string[] lines = raw.Split('\n');
        if (lines.Length < 4 || lines[0].Trim() != "---")
        {
            throw new InvalidOperationException("missing metadata header");
        }
        int end = -1;
        Dictionary<string, string> metadata = new(StringComparer.Ordinal);
        for (int index = 1; index < lines.Length; index++)
        {
            string line = lines[index].Trim();
            if (line == "---")
            {
                end = index;
                break;
            }
            int colon = line.IndexOf(':');
            if (colon < 0)
            {
                throw new InvalidOperationException($"invalid metadata line {line}");
            }
            string key = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();
            if (key.Length == 0 || value.Length == 0)
            {
                throw new InvalidOperationException($"invalid metadata line {line}");
            }
            if (key is not ("id" or "version"))
            {
                throw new InvalidOperationException($"unsupported metadata field {key}");
            }
            if (!metadata.TryAdd(key, value))
            {
                throw new InvalidOperationException($"duplicate metadata field {key}");
            }
        }
        if (end < 0)
        {
            throw new InvalidOperationException("metadata header is not closed");
        }
        string id = metadata.TryGetValue("id", out string? idValue) && idValue.Length > 0
            ? idValue
            : throw new InvalidOperationException("policy id is required");
        if (!int.TryParse(metadata["version"], out int version) || version <= 0)
        {
            throw new InvalidOperationException("policy version must be a positive integer");
        }
        string text = string.Join("\n", lines[(end + 1)..]).Trim();
        if (text.Length == 0)
        {
            throw new InvalidOperationException("policy body is empty");
        }
        return (id, version, text);
    }
}
