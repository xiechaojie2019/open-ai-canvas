#nullable enable
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;

namespace OpenAICanvas.Application.Capabilities;

/// <summary>
/// 渠道模型能力合同的持久化恢复（从 channel_models 记录重建）。
/// Admission 创建校验与 Worker 执行端注入共用同一语义，避免两处漂移。
/// </summary>
public static class ChannelModelCapability
{
    /// <summary>从持久化记录恢复权威能力合同（与 ModelCatalogService 共用语义）。</summary>
    public static ModelCapabilityConfig? NormalizedChannelModelCapability(ChannelModel channelModel)
    {
        string capability = CapabilitySpecOps.NormalizeCapability(channelModel.Capability);
        if (capability == "audio")
        {
            return null;
        }
        if (capability is not ("text" or "image" or "video"))
        {
            throw new InvalidOperationException($"不支持的渠道模型能力：{channelModel.Capability}");
        }
        ModelCapabilityConfig? config;
        try
        {
            config = ModelCapabilityConfigOps.DecodeModelCapabilityConfig(channelModel.CapabilityConfigJSON);
        }
        catch (AppError error)
        {
            throw new InvalidOperationException($"解析渠道模型能力配置失败：{error.Message}");
        }
        return ModelCapabilityConfigOps.NormalizeModelCapabilityConfigForModel(
            capability,
            channelModel.Protocol,
            LogicalModelService.FirstNonEmpty(channelModel.ProviderModelKey, channelModel.ModelKey),
            config);
    }

    /// <summary>
    /// 把权威能力合同的 video 段投影到执行端镜像。
    /// Providers 层不能引用 Application，字段与 json tag 是刻意镜像，需要显式映射。
    /// </summary>
    public static OpenAICanvas.Providers.VideoCapabilityConfig? MirrorVideoConfig(
        ModelCapabilityConfig? config)
    {
        VideoCapabilityConfig? video = config?.Video;
        if (video is null)
        {
            return null;
        }
        return new OpenAICanvas.Providers.VideoCapabilityConfig
        {
            References = new OpenAICanvas.Providers.VideoReferenceConfig
            {
                PromptMaxChars = video.References.PromptMaxChars,
                MinImages = video.References.MinImages,
                MaxImages = video.References.MaxImages,
                MaxImageBytes = video.References.MaxImageBytes,
                MaxVideos = video.References.MaxVideos,
                MaxVideoBytes = video.References.MaxVideoBytes,
                MaxAudios = video.References.MaxAudios,
                MaxAudioBytes = video.References.MaxAudioBytes,
            },
            Resolutions = [.. video.Resolutions],
            GenerateAudio = new OpenAICanvas.Providers.VideoBooleanConfig
            {
                Supported = video.GenerateAudio.Supported,
                Default = video.GenerateAudio.Default,
            },
            Watermark = new OpenAICanvas.Providers.VideoBooleanConfig
            {
                Supported = video.Watermark.Supported,
                Default = video.Watermark.Default,
            },
        };
    }
}
