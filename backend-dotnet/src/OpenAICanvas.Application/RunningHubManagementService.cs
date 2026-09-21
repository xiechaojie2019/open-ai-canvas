#nullable enable
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Outbound;
using OpenAICanvas.Providers;

namespace OpenAICanvas.Application;

/// <summary>RunningHub 管理代理请求。对应 Go: <c>RunningHubWorkflowFetchRequest</c>。</summary>
public sealed class RunningHubFetchRequestDto
{
    [JsonPropertyName("baseUrl")]
    public string BaseURL { get; set; } = "";

    [JsonPropertyName("apiKey")]
    public string APIKey { get; set; } = "";

    [JsonPropertyName("walletApiKey")]
    public string WalletAPIKey { get; set; } = "";

    [JsonPropertyName("useWallet")]
    public bool UseWallet { get; set; }

    [JsonPropertyName("workflowId")]
    public string WorkflowID { get; set; } = "";

    [JsonPropertyName("webappId")]
    public string WebappID { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("capability")]
    public string Capability { get; set; } = "";
}

/// <summary>
/// 独立工作流 Provider 的管理代理：拉取 RunningHub Workflow / App 参数并推断字段。
/// 对应 Go: <c>runninghub_management.go</c>。不复用 ModelChannel。
/// </summary>
public sealed class RunningHubManagementService
{
    public RunningHubManagementService()
    {
    }

    /// <summary>拉取 API 工作流参数。对应 Go: <c>FetchRunningHubWorkflowInfo</c>。</summary>
    public async Task<Dictionary<string, object?>> FetchWorkflowInfoAsync(
        RunningHubFetchRequestDto request, CancellationToken cancellationToken = default)
    {
        string workflowID = request.WorkflowID.Trim();
        if (workflowID.Length == 0)
        {
            throw AppError.BadAuthRequest("workflowId 不能为空");
        }
        (ProviderConfig config, string root) = await ResolveConfigAsync(request).ConfigureAwait(false);
        Dictionary<string, object?> body = new(StringComparer.Ordinal)
        {
            ["apiKey"] = ProviderWorkflowValues.RunningHubApiKey(config),
            ["workflowId"] = workflowID,
        };
        Dictionary<string, object?> response;
        try
        {
            response = await PostJsonAsync(
                config, root + "/api/openapi/getJsonApiFormat", body, cancellationToken).ConfigureAwait(false);
        }
        catch (AppError)
        {
            throw;
        }
        catch (Exception error)
        {
            throw AppError.Wrap(502, "拉取 RunningHub 工作流参数失败：" + error.Message, error);
        }
        if (response.TryGetValue("data", out object? dataValue) && dataValue is Dictionary<string, object?> data)
        {
            Dictionary<string, object?> workflow = ParseWorkflowMap(data.TryGetValue("prompt", out object? promptValue) ? promptValue : null);
            List<WorkflowField> fields = WorkflowProviderManagement.FieldsFromManagement(workflow, request.Capability);
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["workflowId"] = workflowID,
                ["kind"] = "workflow",
                ["title"] = FirstNonEmpty(request.Title, workflowID),
                ["fields"] = fields,
                ["workflowJson"] = workflow,
                ["raw"] = response,
            };
        }
        throw new InvalidOperationException("RunningHub 工作流参数响应缺少 data");
        static Dictionary<string, object?> ParseWorkflowMap(object? prompt)
        {
            string text = WorkflowFieldCodec.GenericString(prompt).Trim();
            if (text.Length > 0)
            {
                try
                {
                    if (JsonFields.FromElement(JsonDocument.Parse(text).RootElement) is Dictionary<string, object?> parsed)
                    {
                        return parsed;
                    }
                }
                catch (JsonException error)
                {
                    throw new InvalidOperationException("RunningHub 工作流 JSON 解析失败：" + error.Message);
                }
                throw new InvalidOperationException("RunningHub 工作流 JSON 解析失败：顶层不是对象");
            }
            if (prompt is Dictionary<string, object?> promptMap)
            {
                return promptMap;
            }
            return [];
        }
    }

    /// <summary>拉取 AI App 参数。对应 Go: <c>FetchRunningHubAppInfo</c>。</summary>
    public async Task<Dictionary<string, object?>> FetchAppInfoAsync(
        RunningHubFetchRequestDto request, CancellationToken cancellationToken = default)
    {
        string webappID = request.WebappID.Trim();
        if (webappID.Length == 0)
        {
            throw AppError.BadAuthRequest("webappId 不能为空");
        }
        (ProviderConfig config, string root) = await ResolveConfigAsync(request).ConfigureAwait(false);
        Dictionary<string, object?> body = new(StringComparer.Ordinal)
        {
            ["apiKey"] = ProviderWorkflowValues.RunningHubApiKey(config),
            ["webappId"] = webappID,
        };
        Dictionary<string, object?> response;
        try
        {
            response = await PostJsonAsync(
                config, root + "/api/webapp/apiCallDemo", body, cancellationToken).ConfigureAwait(false);
        }
        catch (AppError)
        {
            throw;
        }
        catch (Exception error)
        {
            throw AppError.Wrap(502, "拉取 RunningHub App 参数失败：" + error.Message, error);
        }
        (int code, bool valid) = ProviderWorkflowValues.RunningHubPayloadCode(response);
        if (valid && code != 0)
        {
            throw AppError.Wrap(
                502,
                "拉取 RunningHub App 参数失败：" + ProviderWorkflowValues.RunningHubFailureMessage(response),
                null);
        }
        Dictionary<string, object?> result = new(StringComparer.Ordinal)
        {
            ["kind"] = "app",
            ["webappId"] = webappID,
            ["workflowId"] = webappID,
            ["title"] = FirstNonEmpty(request.Title, webappID),
            ["fields"] = Array.Empty<object>(),
            ["raw"] = response,
        };
        if (response.TryGetValue("data", out object? dataValue) && dataValue is Dictionary<string, object?> data)
        {
            List<Dictionary<string, object?>> nodeInfoList = [];
            if (data.TryGetValue("nodeInfoList", out object? raw) && raw is List<object?> items)
            {
                foreach (object? item in items)
                {
                    if (item is Dictionary<string, object?> entry)
                    {
                        nodeInfoList.Add(entry);
                    }
                }
            }
            result["fields"] = WorkflowProviderManagement.FieldsFromAppNodeInfo(nodeInfoList, request.Capability);
        }
        return result;
    }

    /// <summary>对应 Go: <c>runningHubManagementConfig</c>。SSRF 校验 + API Key 必填。</summary>
    private static async Task<(ProviderConfig Config, string Root)> ResolveConfigAsync(
        RunningHubFetchRequestDto request)
    {
        ProviderConfig config = new()
        {
            BaseURL = request.BaseURL.Trim(),
            APIKey = request.APIKey.Trim(),
            RunningHubWalletKey = request.WalletAPIKey.Trim(),
            RunningHubUseWallet = request.UseWallet,
        };
        if (config.BaseURL.Length == 0)
        {
            config.BaseURL = "https://www.runninghub.cn";
        }
        await OutboundGuard.ValidateOutboundUrlAsync(
            ProviderWorkflowValues.RunningHubRootURL(config.BaseURL)).ConfigureAwait(false);
        if (ProviderWorkflowValues.RunningHubApiKey(config).Length == 0)
        {
            throw AppError.BadAuthRequest("请先填写 RunningHub 积分 API Key");
        }
        return (config, ProviderWorkflowValues.RunningHubRootURL(config.BaseURL));
    }

    /// <summary>等价 Go: <c>s.runningHubJSON</c> 的 POST + JSON 解析（ProviderTransport 公共入口）。</summary>
    private static async Task<Dictionary<string, object?>> PostJsonAsync(
        ProviderConfig config, string url, Dictionary<string, object?> body, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, url)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        ProviderTransport.ApplyDefaultHeaders(request);
        return await ProviderTransport.SendJsonAsync(request, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static string FirstNonEmpty(string left, string right)
    {
        string value = left.Trim();
        return value.Length > 0 ? value : right;
    }
}
