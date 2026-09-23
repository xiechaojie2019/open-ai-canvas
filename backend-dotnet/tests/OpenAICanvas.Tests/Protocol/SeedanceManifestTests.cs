#nullable enable

using System.IO.Compression;
using OpenAICanvas.Protocol;
using Xunit;

namespace OpenAICanvas.Tests.Protocol;

public sealed class SeedanceManifestTests
{
    private const string FastModel = "doubao-seedance-2-0-fast-260128";
    private const string RegularModel = "doubao-seedance-2-0-260128";

    [Theory]
    [InlineData(RegularModel, 0, true)]
    [InlineData(RegularModel, 4, true)]
    [InlineData(RegularModel, 12, true)]
    [InlineData(RegularModel, 1, false)]
    [InlineData(RegularModel, 13, false)]
    [InlineData(FastModel, 0, true)]
    [InlineData(FastModel, 1, true)]
    [InlineData(FastModel, 15, true)]
    [InlineData(FastModel, 16, false)]
    public void Seedance_按模型应用时长校验(string model, int duration, bool valid)
    {
        IProtocolAdapter adapter = LoadAdapter();
        RequestContext context = new()
        {
            BaseURL = "https://ark.cn-beijing.volces.com",
            Request = new GenerationRequest
            {
                Capability = ProtocolCapability.Video,
                Model = model,
                Prompt = "一只猫在窗边走过",
                Duration = duration,
            },
        };

        if (valid)
        {
            RequestSpec request = adapter.BuildCreate(context);
            Assert.Equal("POST", request.Method);
            Assert.Equal("/api/v3/contents/generations/tasks", request.Path);
        }
        else
        {
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => adapter.BuildCreate(context));
            Assert.Contains("时长", error.Message);
        }
    }

    private static IProtocolAdapter LoadAdapter()
    {
        string? current = AppContext.BaseDirectory;
        for (int depth = 0; depth < 10 && current is not null; depth++)
        {
            string packagePath = Path.Combine(current, "plugin-packages", "volcengine-ark-seedance.yingce-plugin");
            if (File.Exists(packagePath))
            {
                using ZipArchive archive = ZipFile.OpenRead(packagePath);
                ZipArchiveEntry entry = archive.GetEntry("manifest.json")
                    ?? throw new InvalidOperationException("Seedance manifest.json 不存在");
                using Stream stream = entry.Open();
                using MemoryStream buffer = new();
                stream.CopyTo(buffer);
                return ProtocolManifestCodec.LoadInstalledProviders(buffer.ToArray(), null).Single();
            }
            current = Path.GetDirectoryName(current);
        }
        throw new InvalidOperationException("找不到 Seedance 插件包");
    }
}
