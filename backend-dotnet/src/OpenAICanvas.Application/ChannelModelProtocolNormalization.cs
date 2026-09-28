#nullable enable
using OpenAICanvas.Domain.Entities;

namespace OpenAICanvas.Application;

/// <summary>
/// 归一化渠道模型的协议合同，避免旧配置中的渠道格式与模型协议组合导致错误的出站路径。
/// </summary>
internal static class ChannelModelProtocolNormalization
{
    public static string Normalize(string channelApiFormat, string capability, string protocol)
    {
        string apiFormat = channelApiFormat.Trim();
        string normalizedCapability = capability.Trim().ToLowerInvariant();
        string normalizedProtocol = protocol.Trim();

        if (apiFormat.Equals("openai", StringComparison.OrdinalIgnoreCase)
            && normalizedCapability == "image"
            && normalizedProtocol.Equals("adobe-firefly", StringComparison.OrdinalIgnoreCase))
        {
            return ChannelInterfaceType.ChannelInterfaceOpenAIImage;
        }

        return normalizedProtocol;
    }
}
