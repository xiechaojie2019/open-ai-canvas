#nullable enable
using OpenAICanvas.Outbound;
using OpenAICanvas.Providers;
using Xunit;

namespace OpenAICanvas.Tests.Providers;

/// <summary>
/// Provider 媒体编码的契约测试。
/// 对应 Go: <c>internal/app/provider_http_client.go</c> 的
/// <c>mediaBytes</c> / <c>providerMediaFilename</c> / <c>writeMediaPart</c>，
/// 以及 <c>parseRetryAfter</c>。
/// </summary>
public sealed class ProviderMediaTests
{
    private static readonly byte[] PngHeader =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    // ------------------------------------------------------------ mediaBytes

    [Fact]
    public void 媒体字节_解析dataURL并嗅探类型()
    {
        string dataUrl = "data:image/png;base64," + Convert.ToBase64String(PngHeader);
        (byte[] raw, string mimeType) = ProviderMediaCodec.Bytes(new ProviderMedia { DataURL = dataUrl });

        Assert.Equal(PngHeader, raw);
        Assert.Equal("image/png", mimeType);
    }

    [Fact]
    public void 媒体字节_声明类型优先于嗅探()
    {
        // 声明为 webp 但内容是 PNG：声明值优先（与 Go 一致）。
        string dataUrl = "data:image/webp;base64," + Convert.ToBase64String(PngHeader);
        (_, string mimeType) = ProviderMediaCodec.Bytes(new ProviderMedia { DataURL = dataUrl });

        Assert.Equal("image/webp", mimeType);
    }

    [Fact]
    public void 媒体字节_octet流声明时回落到嗅探()
    {
        // application/octet-stream 视为"未声明"，按内容嗅探。
        string dataUrl = "data:application/octet-stream;base64," + Convert.ToBase64String(PngHeader);
        (_, string mimeType) = ProviderMediaCodec.Bytes(new ProviderMedia { DataURL = dataUrl });

        Assert.Equal("image/png", mimeType);
    }

    [Fact]
    public void 媒体字节_空声明时用mediaType回落()
    {
        string dataUrl = "data:;base64," + Convert.ToBase64String(PngHeader);
        (_, string mimeType) = ProviderMediaCodec.Bytes(new ProviderMedia
        {
            DataURL = dataUrl,
            Type = "image/webp",
        });

        Assert.Equal("image/webp", mimeType);
    }

    [Fact]
    public void 媒体字节_URL字段作为dataUrl的回落()
    {
        string dataUrl = "data:image/png;base64," + Convert.ToBase64String(PngHeader);
        (byte[] raw, _) = ProviderMediaCodec.Bytes(new ProviderMedia { URL = dataUrl });

        Assert.Equal(PngHeader, raw);
    }

    [Fact]
    public void 媒体字节_非dataURL报错()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ProviderMediaCodec.Bytes(new ProviderMedia { URL = "https://example.com/a.png" }));
        Assert.Contains("data URL", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 媒体字节_缺少逗号报格式错误()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ProviderMediaCodec.Bytes(new ProviderMedia { DataURL = "data:image/png;base64" }));
        Assert.Contains("data URL 格式错误", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 媒体字节_非法base64报错()
    {
        Assert.Throws<InvalidOperationException>(
            () => ProviderMediaCodec.Bytes(new ProviderMedia { DataURL = "data:image/png;base64,!!!notbase64!!!" }));
    }

    // ------------------------------------------------------------ 文件名

    [Fact]
    public void 文件名_按MIME推断扩展名()
    {
        Assert.Equal("reference-photo.png",
            ProviderMediaCodec.MediaFilename(new ProviderMedia { ID = "photo" }, "image/png"));
        Assert.Equal("reference-clip.mp4",
            ProviderMediaCodec.MediaFilename(new ProviderMedia { ID = "clip" }, "video/mp4"));
        Assert.Equal("reference-song.mp3",
            ProviderMediaCodec.MediaFilename(new ProviderMedia { ID = "song" }, "audio/mpeg"));
    }

    [Fact]
    public void 文件名_未知类型兜底bin()
    {
        Assert.Equal("reference-x.bin",
            ProviderMediaCodec.MediaFilename(new ProviderMedia { ID = "x" }, "application/x-custom"));
    }

    [Fact]
    public void 文件名_过滤不安全字符()
    {
        // 路径分隔符、空格、点号等一律剔除，只保留 [A-Za-z0-9-_]。
        // "a/b c\d-_.png" -> 保留 a b c d - _ p n g -> "abcd-_png"，扩展名由 MIME 追加。
        Assert.Equal("reference-abcd-_png.png",
            ProviderMediaCodec.MediaFilename(new ProviderMedia { ID = "a/b c\\d-_.png" }, "image/png"));

        // "../.." 全被过滤 -> 回落 reference。
        Assert.Equal("reference-reference.png",
            ProviderMediaCodec.MediaFilename(new ProviderMedia { ID = "../.." }, "image/png"));
    }

    [Fact]
    public void 文件名_空ID兜底reference()
    {
        Assert.Equal("reference-reference.png",
            ProviderMediaCodec.MediaFilename(new ProviderMedia(), "image/png"));
        // 非空但全被过滤掉时同样兜底。
        Assert.Equal("reference-reference.png",
            ProviderMediaCodec.MediaFilename(new ProviderMedia { ID = "///" }, "image/png"));
    }

    [Fact]
    public void 文件名_ID截断到64字符()
    {
        ProviderMedia media = new() { ID = new string('a', 100) };
        string filename = ProviderMediaCodec.MediaFilename(media, "image/png");

        // "reference-" + 64 个 a + ".png"
        Assert.Equal("reference-" + new string('a', 64) + ".png", filename);
    }

    [Fact]
    public void 文件名_带参数的MIME按主类型取扩展名()
    {
        Assert.Equal("reference-a.png",
            ProviderMediaCodec.MediaFilename(new ProviderMedia { ID = "a" }, "image/png; charset=binary"));
    }

    // ------------------------------------------------------------ multipart

    [Fact]
    public async Task 写入多媒体part_带文件名与内容类型()
    {
        using MultipartFormDataContent form = new();
        ProviderMedia media = new()
        {
            ID = "ref-1",
            DataURL = "data:image/png;base64," + Convert.ToBase64String(PngHeader),
        };

        await ProviderMediaCodec.WriteMediaPartAsync(form, "image", media);

        Assert.Single(form, part =>
            part.Headers.ContentType?.MediaType == "image/png" &&
            part.Headers.ContentDisposition?.Name?.Trim('"') == "image" &&
            part.Headers.ContentDisposition.FileName?.Trim('"') == "reference-ref-1.png");

        byte[] serialized = await form.ReadAsByteArrayAsync();
        string text = System.Text.Encoding.UTF8.GetString(serialized);
        Assert.Contains("name=image", text, StringComparison.Ordinal);
        Assert.Contains("filename=reference-ref-1.png", text, StringComparison.Ordinal);
        Assert.Contains("Content-Type: image/png", text, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ Retry-After

    [Fact]
    public void RetryAfter_秒数解析()
    {
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        Assert.Equal(TimeSpan.FromSeconds(120), OutboundHttpClient.ParseRetryAfter("120", now));
        Assert.Equal(TimeSpan.FromSeconds(1), OutboundHttpClient.ParseRetryAfter("1", now));
        // 前后空白容忍。
        Assert.Equal(TimeSpan.FromSeconds(5), OutboundHttpClient.ParseRetryAfter("  5  ", now));
    }

    [Fact]
    public void RetryAfter_零与负数与空值归零()
    {
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        Assert.Equal(TimeSpan.Zero, OutboundHttpClient.ParseRetryAfter("0", now));
        Assert.Equal(TimeSpan.Zero, OutboundHttpClient.ParseRetryAfter("-5", now));
        Assert.Equal(TimeSpan.Zero, OutboundHttpClient.ParseRetryAfter("", now));
        Assert.Equal(TimeSpan.Zero, OutboundHttpClient.ParseRetryAfter(null, now));
        Assert.Equal(TimeSpan.Zero, OutboundHttpClient.ParseRetryAfter("   ", now));
    }

    [Fact]
    public void RetryAfter_输出日期解析()
    {
        DateTimeOffset now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset later = now.AddSeconds(90);
        Assert.Equal(TimeSpan.FromSeconds(90),
            OutboundHttpClient.ParseRetryAfter(later.ToString("R"), now));
    }

    [Fact]
    public void RetryAfter_已过期的日期归零()
    {
        DateTimeOffset now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset past = now.AddSeconds(-90);
        Assert.Equal(TimeSpan.Zero, OutboundHttpClient.ParseRetryAfter(past.ToString("R"), now));
    }

    [Fact]
    public void RetryAfter_无法解析归零()
    {
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        Assert.Equal(TimeSpan.Zero, OutboundHttpClient.ParseRetryAfter("soon", now));
        Assert.Equal(TimeSpan.Zero, OutboundHttpClient.ParseRetryAfter("120s", now));
        Assert.Equal(TimeSpan.Zero, OutboundHttpClient.ParseRetryAfter("+", now));
        // Atoi 拒绝小数（不走 http.ParseTime 分支，因为该分支的解析结果早于 now）。
        Assert.Equal(TimeSpan.Zero, OutboundHttpClient.ParseRetryAfter("1.5x", now));
    }

    [Fact]
    public void RetryAfter_可被httpParseTime识别的字符串按日期处理()
    {
        // 与 Go 一致："1.5" 会被 http.ParseTime 当作日期解析出极早/极晚的时刻。
        // 这里只断言"不抛异常"，行为交由上游格式决定。
        DateTimeOffset now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        TimeSpan parsed = OutboundHttpClient.ParseRetryAfter("1.5", now);
        Assert.True(parsed >= TimeSpan.Zero);
    }

    // ------------------------------------------------------------ 嗅探表

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, "image/png")]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, "image/jpeg")]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }, "image/gif")]
    [InlineData(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31 }, "application/pdf")]
    [InlineData(new byte[] { 0x1A, 0x45, 0xDF, 0xA3, 0x01 }, "video/webm")]
    [InlineData(new byte[] { 0x49, 0x44, 0x33, 0x03 }, "audio/mpeg")]
    [InlineData(new byte[] { 0x42, 0x4D, 0x00 }, "image/bmp")]
    public void 嗅探表_识别常见媒体(byte[] data, string expected) =>
        Assert.Equal(expected, ContentTypeSniffer.Sniff(data));

    [Fact]
    public void 嗅探表_ftyp判定为mp4()
    {
        byte[] mp4 = new byte[16];
        mp4[4] = (byte)'f';
        mp4[5] = (byte)'t';
        mp4[6] = (byte)'y';
        mp4[7] = (byte)'p';

        Assert.Equal("video/mp4", ContentTypeSniffer.Sniff(mp4));
    }

    [Fact]
    public void 嗅探表_未知二进制归octet流()
    {
        Assert.Equal("application/octet-stream",
            ContentTypeSniffer.Sniff(new byte[] { 0x00, 0x01, 0x02, 0x03 }));
        // 空数据无法判定为文本。
        Assert.Equal("application/octet-stream", ContentTypeSniffer.Sniff(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void 嗅探表_纯文本判为文本()
    {
        Assert.Equal("text/plain; charset=utf-8",
            ContentTypeSniffer.Sniff(System.Text.Encoding.UTF8.GetBytes("hello world")));
    }

    // ------------------------------------------------------------ 归一化

    [Fact]
    public void MIME归一化_声明优先否则嗅探()
    {
        Assert.Equal("image/custom", ProviderMediaCodec.NormalizedMediaMimeType("image/custom", PngHeader));
        Assert.Equal("image/png",
            ProviderMediaCodec.NormalizedMediaMimeType("application/octet-stream", PngHeader));
        Assert.Equal("image/png", ProviderMediaCodec.NormalizedMediaMimeType("", PngHeader));
        // 带参数时取主类型。
        Assert.Equal("image/custom",
            ProviderMediaCodec.NormalizedMediaMimeType("image/custom; q=1", PngHeader));
    }
}
