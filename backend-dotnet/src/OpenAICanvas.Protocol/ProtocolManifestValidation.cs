#nullable enable

using System.Text;

namespace OpenAICanvas.Protocol;

/// <summary>
/// 插件清单的校验与归一化。对应 Go: <c>internal/protocol/manifest.go</c> 的
/// <c>ValidateManifest</c>/<c>validatePluginMetadata</c>/<c>validatePaymentProviderContributions</c>/
/// <c>normalizeManifest</c>/<c>normalizeManifestForProvider</c>/<c>operationSummary</c>/
/// <c>validateManifestOperation</c>/<c>hasNonProviderContribution</c>。
/// </summary>
/// <remarks>
/// 失败一律抛 <see cref="InvalidOperationException"/>，消息文本与 Go 逐字对齐：
/// 插件作者按这些文本排错，改动会让既有插件的诊断信息失配。
/// </remarks>
public static class ProtocolManifestValidation
{
    private const int ManifestNameMaxBytes = 160;
    private const int ManifestVendorMaxBytes = 120;

    private static readonly string[] SupportedAPIVersions = ["yingce.plugin/v1", "yingce.plugin/v2"];

    private static readonly string[] SupportedBackends =
        ["", "declarative", "rpc", "wasm", "trusted-backend"];

    private static readonly string[] SupportedContentTypes =
        ["application/json", "multipart/form-data", "application/x-www-form-urlencoded", "application/octet-stream", ""];

    /// <summary>对应 Go: <c>ValidateManifest</c>。</summary>
    public static void Validate(Manifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (!SupportedAPIVersions.Contains(manifest.APIVersion.Trim(), StringComparer.Ordinal))
        {
            throw new InvalidOperationException($"unsupported protocol manifest apiVersion \"{manifest.APIVersion}\"");
        }
        if (manifest.Metadata.ID.Trim().Length == 0 || manifest.Metadata.Version.Trim().Length == 0)
        {
            throw new InvalidOperationException("protocol manifest metadata requires id and version");
        }
        if (manifest.Metadata.Name.Trim().Length == 0)
        {
            throw new InvalidOperationException("protocol manifest metadata requires name");
        }
        if (!ProtocolManifestValues.IsValidIdentifier(manifest.Metadata.ID))
        {
            throw new InvalidOperationException("protocol manifest metadata id is invalid");
        }
        // Go 用 len() 比较，这里是字节长度而不是字符数。
        if (Encoding.UTF8.GetByteCount(manifest.Metadata.Name) > ManifestNameMaxBytes ||
            Encoding.UTF8.GetByteCount(manifest.Metadata.Vendor) > ManifestVendorMaxBytes)
        {
            throw new InvalidOperationException("protocol manifest metadata name or vendor is too long");
        }
        if (manifest.Contributes.Providers.Count == 0 && !HasNonProviderContribution(manifest.Contributes))
        {
            throw new InvalidOperationException("plugin must declare at least one contribution");
        }
        string backend = manifest.Runtime.Backend.Trim();
        if (!SupportedBackends.Contains(backend, StringComparer.Ordinal) &&
            !backend.StartsWith("host:", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"unsupported plugin backend \"{manifest.Runtime.Backend}\"");
        }
        if (manifest.Contributes.Providers.Count == 0)
        {
            ValidatePaymentProviderContributions(manifest);
            return;
        }

        HashSet<string> providerIDs = new(StringComparer.Ordinal);
        for (int index = 0; index < manifest.Contributes.Providers.Count; index++)
        {
            ManifestProvider provider = manifest.Contributes.Providers[index];
            if (provider.ID.Trim().Length == 0 || provider.Label.Trim().Length == 0 || !ProtocolManifestValues.IsValidIdentifier(provider.ID))
            {
                throw new InvalidOperationException($"provider contribution {index} requires a valid id and label");
            }
            if (!providerIDs.Add(provider.ID))
            {
                throw new InvalidOperationException($"duplicate provider contribution \"{provider.ID}\"");
            }
            if (provider.Capabilities.Count == 0 || provider.Scopes.Count == 0)
            {
                throw new InvalidOperationException($"provider contribution \"{provider.ID}\" requires capabilities and scopes");
            }
            foreach (string capability in provider.Capabilities)
            {
                if (!IsSupportedCapability(capability))
                {
                    throw new InvalidOperationException($"unsupported protocol capability \"{capability}\"");
                }
            }
            foreach (string scope in provider.Scopes)
            {
                if (!IsSupportedScope(scope))
                {
                    throw new InvalidOperationException($"unsupported protocol scope \"{scope}\"");
                }
            }
            ValidateOperation(provider.Create, $"provider \"{provider.ID}\" create operation");
            if (provider.Agent is not null)
            {
                ValidateOperation(provider.Agent, $"provider \"{provider.ID}\" agent operation");
                if (provider.AgentResponse is null)
                {
                    throw new InvalidOperationException(
                        $"provider \"{provider.ID}\" agent response mapping is required when agent operation is declared");
                }
            }
            if (provider.Poll is not null)
            {
                ValidateOperation(provider.Poll, $"provider \"{provider.ID}\" poll operation");
            }
            if (provider.Cancel is not null)
            {
                ValidateOperation(provider.Cancel, $"provider \"{provider.ID}\" cancel operation");
            }
            if (provider.Result is not null)
            {
                ValidateOperation(provider.Result, $"provider \"{provider.ID}\" result operation");
            }
            for (int ruleIndex = 0; ruleIndex < provider.Validations.Count; ruleIndex++)
            {
                ManifestValidation rule = provider.Validations[ruleIndex];
                if (rule.Assert is null || rule.Message.Trim().Length == 0)
                {
                    throw new InvalidOperationException(
                        $"provider \"{provider.ID}\" validation {ruleIndex} requires assert and message");
                }
            }
        }
        ValidatePaymentProviderContributions(manifest);
    }

    /// <summary>对应 Go: <c>validatePluginMetadata</c>。</summary>
    public static void ValidatePluginMetadata(Metadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        if (metadata.ID.Trim().Length == 0 || metadata.Version.Trim().Length == 0 ||
            !ProtocolManifestValues.IsValidIdentifier(metadata.ID))
        {
            throw new InvalidOperationException("protocol plugin metadata is invalid");
        }
        if (metadata.Categories.Count == 0 || metadata.Scopes.Count == 0)
        {
            throw new InvalidOperationException("protocol plugin metadata requires categories and scopes");
        }
        foreach (string capability in metadata.Categories)
        {
            if (!IsSupportedCapability(capability))
            {
                throw new InvalidOperationException($"unsupported protocol capability \"{capability}\"");
            }
        }
        foreach (string scope in metadata.Scopes)
        {
            if (!IsSupportedScope(scope))
            {
                throw new InvalidOperationException($"unsupported protocol scope \"{scope}\"");
            }
        }
    }

    /// <summary>对应 Go: <c>validatePaymentProviderContributions</c>。</summary>
    public static void ValidatePaymentProviderContributions(Manifest manifest)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        for (int index = 0; index < manifest.Contributes.PaymentProviders.Count; index++)
        {
            ManifestPaymentProvider provider = manifest.Contributes.PaymentProviders[index];
            // Go 遍历的是值拷贝，trim 只作用于局部变量，不回写清单。
            string id = provider.ID.Trim();
            if (id.Length == 0 || provider.Label.Trim().Length == 0 || !ProtocolManifestValues.IsValidIdentifier(id))
            {
                throw new InvalidOperationException($"payment provider contribution {index} requires a valid id and label");
            }
            if (!seen.Add(id))
            {
                throw new InvalidOperationException($"duplicate payment provider contribution \"{id}\"");
            }
            if (provider.Icon.Trim().Length == 0)
            {
                throw new InvalidOperationException($"payment provider contribution \"{id}\" requires an icon");
            }
            if (provider.CheckoutMode is not ("qr_code" or "redirect"))
            {
                throw new InvalidOperationException(
                    $"payment provider contribution \"{id}\" has unsupported checkout mode \"{provider.CheckoutMode}\"");
            }
            ManifestPaymentExpiryPolicy policy = provider.ExpiryPolicy;
            if (policy.MinMinutes <= 0 || policy.DefaultMinutes < policy.MinMinutes || policy.MaxMinutes < policy.DefaultMinutes)
            {
                throw new InvalidOperationException($"payment provider contribution \"{id}\" has invalid expiry policy");
            }
            foreach (string field in provider.IdentityFields)
            {
                if (field.Trim().Length == 0 || field.Length > 80)
                {
                    throw new InvalidOperationException($"payment provider contribution \"{id}\" has invalid identity field \"{field}\"");
                }
            }
            foreach (ManifestPaymentResponse response in new[] { provider.NotificationSuccess, provider.NotificationFailure })
            {
                if (response.Status < 0 || response.Status > 599)
                {
                    throw new InvalidOperationException(
                        $"payment provider contribution \"{id}\" has invalid notification response status");
                }
            }
        }
    }

    /// <summary>对应 Go: <c>normalizeManifest</c>。</summary>
    public static void Normalize(Manifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (manifest.Contributes.Providers.Count == 0)
        {
            manifest.Metadata.Execution = manifest.Runtime.Backend;
            return;
        }
        NormalizeForProvider(manifest, 0);
    }

    /// <summary>
    /// 把第 <paramref name="index"/> 个 provider 贡献点提升为可执行的 provider 投影。
    /// 对应 Go: <c>normalizeManifestForProvider</c>。
    /// </summary>
    public static void NormalizeForProvider(Manifest manifest, int index)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (index < 0 || index >= manifest.Contributes.Providers.Count)
        {
            throw new InvalidOperationException("plugin provider contribution is missing");
        }
        ManifestProvider provider = manifest.Contributes.Providers[index];
        manifest.Metadata.ID = provider.ID;
        manifest.Metadata.Categories = provider.Capabilities;
        manifest.Metadata.Scopes = provider.Scopes;
        manifest.Metadata.Parameters = provider.Parameters;
        manifest.Metadata.Create = OperationSummary(provider.Create);
        manifest.Metadata.Poll = provider.Poll is null ? "" : OperationSummary(provider.Poll);
        manifest.Metadata.Cancel = provider.Cancel is null ? "" : OperationSummary(provider.Cancel);
        manifest.Metadata.ContentType = provider.Create.ContentType;
        manifest.Metadata.RequiresPublicMediaURLs = provider.RequiresPublicMediaURLs;
        manifest.Metadata.Execution = manifest.Runtime.Backend;
        manifest.Create = provider.Create;
        manifest.Agent = provider.Agent;
        manifest.Poll = provider.Poll;
        manifest.Cancel = provider.Cancel;
        manifest.ResultOperation = provider.Result;
        manifest.Response = provider.Response;
        manifest.AgentResponse = provider.AgentResponse;
        manifest.Auth = provider.Auth;
        manifest.Validations = provider.Validations;
    }

    /// <summary>对应 Go: <c>hasNonProviderContribution</c>。</summary>
    public static bool HasNonProviderContribution(ManifestContributions contributes) =>
        contributes.PaymentProviders.Count > 0 ||
        contributes.Workflows.Count > 0 ||
        contributes.CanvasNodes.Count > 0 ||
        contributes.Transforms.Count > 0 ||
        contributes.Commands.Count > 0 ||
        contributes.AssetSources.Count > 0 ||
        contributes.UsageObservers.Count > 0 ||
        contributes.AICapabilities.Count > 0 ||
        contributes.Agents.Count > 0 ||
        contributes.ImportExport.Count > 0;

    /// <summary>对应 Go: <c>operationSummary</c>（管理端展示用的方法 + 路径摘要）。</summary>
    public static string OperationSummary(ManifestOperation operation)
    {
        string path = operation.Path
            .Replace("{{model}}", "{model}", StringComparison.Ordinal)
            .Replace("{{taskId}}", "{task_id}", StringComparison.Ordinal);
        return operation.Method.ToUpperInvariant() + " " + path;
    }

    /// <summary>对应 Go: <c>validateManifestOperation</c>。</summary>
    public static void ValidateOperation(ManifestOperation operation, string label)
    {
        try
        {
            ValidateOperationCore(operation);
        }
        catch (InvalidOperationException error)
        {
            throw new InvalidOperationException($"{label}: {error.Message}", error);
        }
    }

    private static void ValidateOperationCore(ManifestOperation operation)
    {
        string method = operation.Method.Trim().ToUpperInvariant();
        if (method is not ("GET" or "POST" or "DELETE" or "PUT"))
        {
            throw new InvalidOperationException($"unsupported HTTP method \"{operation.Method}\"");
        }
        if (operation.PathTemplate is null && !ProtocolManifestValues.IsRelativePath(operation.Path))
        {
            throw new InvalidOperationException($"path must be relative: \"{operation.Path}\"");
        }
        string contentType = ProtocolManifestValues.DefaultValue(operation.ContentType, "application/json")
            .Split(';')[0]
            .Trim()
            .ToLowerInvariant();
        if (!SupportedContentTypes.Contains(contentType, StringComparer.Ordinal))
        {
            throw new InvalidOperationException($"unsupported content type \"{operation.ContentType}\"");
        }
        if (operation.Files is { Count: > 0 } && operation.ContentTypeTemplate is null && contentType != "multipart/form-data")
        {
            throw new InvalidOperationException("file parts require multipart/form-data");
        }
        foreach (ManifestFilePart file in operation.Files ?? [])
        {
            if (file.Name.Trim().Length == 0 || file.Source is null)
            {
                throw new InvalidOperationException("multipart file part requires name and source");
            }
        }
    }

    private static bool IsSupportedCapability(string capability) =>
        capability is ProtocolCapability.Text or ProtocolCapability.Image or ProtocolCapability.Video or ProtocolCapability.Audio;

    private static bool IsSupportedScope(string scope) =>
        scope is ProtocolSurface.AdminSystemChannel or ProtocolSurface.UserCustomChannel or
        ProtocolSurface.Canvas or ProtocolSurface.Creation or ProtocolSurface.Agent;
}
