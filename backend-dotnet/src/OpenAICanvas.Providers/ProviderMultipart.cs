#nullable enable
using System.Text;

namespace OpenAICanvas.Providers;

/// <summary>
/// 手写 <c>multipart/form-data</c> 构造。
/// 对应 Go 的 <c>writeField</c> / <c>writeMediaPart</c> 与 <c>writer.Close()</c>。
/// </summary>
/// <remarks>
/// 不用 <c>MultipartFormDataContent</c> 是因为需要<b>严格的字段顺序</b>与对 Go
/// <c>mime/multipart</c> 输出格式的逐字节等价（测试与上游都可能依赖顺序）。
/// </remarks>
public static class ProviderMultipart
{
    /// <summary>写一个文本字段。对应 Go: <c>writeField</c>。</summary>
    public static void WriteField(Stream stream, string boundary, string name, string value) =>
        WritePart(stream, boundary, name, null, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(value));

    /// <summary>
    /// 写一个媒体字段（文件名与 MIME 由 <see cref="ProviderMediaCodec"/> 归一化）。
    /// 对应 Go: <c>writeMediaPart</c>。
    /// </summary>
    public static void WriteMedia(Stream stream, string boundary, string name, ProviderMedia media)
    {
        (byte[] raw, string mimeType) = ProviderMediaCodec.Bytes(media);
        string filename = ProviderMediaCodec.MediaFilename(media, mimeType);
        WritePart(stream, boundary, name, filename, mimeType, raw);
    }

    /// <summary>写一个自定义 part。对应 Go: <c>createFormFile</c> + 内容写入。</summary>
    public static void WritePart(
        Stream stream, string boundary, string name, string? filename, string contentType, byte[] content)
    {
        WriteAscii(stream, "--" + boundary + "\r\n");
        string disposition = filename is null
            ? $"Content-Disposition: form-data; name=\"{name}\"\r\n"
            : $"Content-Disposition: form-data; name=\"{name}\"; filename=\"{filename}\"\r\n";
        WriteAscii(stream, disposition);
        WriteAscii(stream, "Content-Type: " + contentType + "\r\n\r\n");
        stream.Write(content);
        WriteAscii(stream, "\r\n");
    }

    /// <summary>写收尾边界。对应 Go: <c>writer.Close()</c>。</summary>
    public static void Close(Stream stream, string boundary) =>
        WriteAscii(stream, "--" + boundary + "--\r\n");

    private static void WriteAscii(Stream stream, string value) =>
        stream.Write(Encoding.UTF8.GetBytes(value));
}
