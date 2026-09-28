#nullable enable
using OpenAICanvas.Application;
using OpenAICanvas.Domain.Entities;
using Xunit;

namespace OpenAICanvas.Tests.Application;

public sealed class ChannelModelProtocolNormalizationTests
{
    [Fact]
    public void OpenAI图片渠道中的旧Firefly协议归一为OpenAIImages()
    {
        string protocol = ChannelModelProtocolNormalization.Normalize(
            "openai", "image", "adobe-firefly");

        Assert.Equal(ChannelInterfaceType.ChannelInterfaceOpenAIImage, protocol);
    }

    [Fact]
    public void 已是OpenAIImages协议时保持不变()
    {
        string protocol = ChannelModelProtocolNormalization.Normalize(
            "openai", "image", "openai-image");

        Assert.Equal("openai-image", protocol);
    }

    [Fact]
    public void 非OpenAI渠道不替换Firefly协议()
    {
        string protocol = ChannelModelProtocolNormalization.Normalize(
            "firefly", "image", "adobe-firefly");

        Assert.Equal("adobe-firefly", protocol);
    }

    [Theory]
    [InlineData("text", "adobe-firefly")]
    [InlineData("video", "adobe-firefly")]
    [InlineData("image", "gemini-image")]
    [InlineData("image", "volcengine-ark-image")]
    public void 其他能力和协议不受影响(string capability, string protocol)
    {
        Assert.Equal(
            protocol,
            ChannelModelProtocolNormalization.Normalize("openai", capability, protocol));
    }
}
