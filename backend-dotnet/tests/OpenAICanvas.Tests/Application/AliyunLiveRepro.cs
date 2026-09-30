#nullable enable
using OpenAICanvas.Application;
using Xunit;

namespace OpenAICanvas.Tests.Application;

/// <summary>用真实凭证复现服务端 TestViaChannelAsync 的通道调用（临时诊断，完成后删除）。</summary>
public sealed class AliyunLiveRepro
{
    [Fact]
    public async Task 通道回环_真实阿里云()
    {
        StorageChannelSettings setting = new()
        {
            Enabled = true,
            Provider = "aliyun",
            Region = "cn-zhangjiakou",
            Endpoint = "https://oss-cn-zhangjiakou.aliyuncs.com",
            Bucket = "dazezb",
            AccessKeyId = "LTAI5tDvr5L6CsEwqzhi6y88",
            AccessKeySecret = File.ReadAllText("secret.txt").Trim(),
            PublicBaseUrl = "",
            PathPrefix = "tools",
            S3Preset = "custom",
        };
        string key = "tools/.yingce-repro-" + Guid.NewGuid().ToString("N") + ".txt";
        byte[] marker = "yingce-storage-test"u8.ToArray();
        Exception? failure = await Record.ExceptionAsync(async () =>
        {
            await StorageObjectChannel.PutObjectAsync(
                setting, key, "text/plain", marker.Length, new MemoryStream(marker), CancellationToken.None).ConfigureAwait(false);
            using var range = await StorageObjectChannel.GetOriginObjectRangeAsync(
                setting, key, "bytes=0-3", CancellationToken.None).ConfigureAwait(false);
            byte[] head = new byte[4];
            int read = await range.Body.ReadAsync(head).ConfigureAwait(false);
            Assert.Equal(4, read);
            await StorageObjectChannel.DeleteObjectAsync(setting, key).ConfigureAwait(false);
        });
        Assert.True(failure is null, failure?.ToString());
    }
}


