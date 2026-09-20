#nullable enable

using System.Text;

namespace OpenAICanvas.Outbound;

/// <summary>
/// 内容类型嗅探（等价 Go 的 <c>http.DetectContentType</c> 常用子集）。
/// </summary>
/// <remarks>
/// 上游 <c>mime</c> 包与资源上传模块共用同一张魔数表；放在 <c>Outbound</c> 层
/// 以避免 <c>Application</c> ↔ <c>Providers</c> 的循环依赖。
/// </remarks>
public static class ContentTypeSniffer
{
    /// <summary>嗅探字节流的 MIME；无法识别时返回 <c>application/octet-stream</c>。</summary>
    public static string Sniff(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 4 && data[0] == 0x00 && data[1] == 0x00 && data[2] == 0x01 && data[3] == 0x00)
        {
            return "image/x-icon";
        }
        if (data.Length >= 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
        {
            return "image/png";
        }
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            return "image/jpeg";
        }
        if (data.Length >= 4 && data[0] == 'G' && data[1] == 'I' && data[2] == 'F' && data[3] == '8')
        {
            return "image/gif";
        }
        if (data.Length >= 12 && data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F'
            && data[8] == 'W' && data[9] == 'E' && data[10] == 'B' && data[11] == 'P')
        {
            return "image/webp";
        }
        if (data.Length >= 5 && data[0] == '%' && data[1] == 'P' && data[2] == 'D' && data[3] == 'F' && data[4] == '-')
        {
            return "application/pdf";
        }
        if (data.Length >= 4 && data[0] == 0x1A && data[1] == 0x45 && data[2] == 0xDF && data[3] == 0xA3)
        {
            return "video/webm";
        }
        if (data.Length >= 12 && data[4] == 'f' && data[5] == 't' && data[6] == 'y' && data[7] == 'p')
        {
            return "video/mp4";
        }
        if (data.Length >= 4 && data[0] == 'I' && data[1] == 'D' && data[2] == '3')
        {
            return "audio/mpeg";
        }
        if (data.Length >= 2 && data[0] == 'B' && data[1] == 'M')
        {
            return "image/bmp";
        }
        if (data.Length >= 4 && data[0] == 'P' && data[1] == 'K' && data[2] == 0x03 && data[3] == 0x04)
        {
            return "application/zip";
        }
        if (data.Length >= 5 && data[0] == '<' && data[1] == '!' && data[2] == 'D' && data[3] == 'O' && data[4] == 'C')
        {
            return "text/html; charset=utf-8";
        }
        if (data.Length >= 4 && data[0] == 'S' && data[1] == 'Q' && data[2] == 'L' && data[3] == 'i')
        {
            return "application/vnd.sqlite3";
        }
        if (IsPlainText(data))
        {
            return "text/plain; charset=utf-8";
        }
        return "application/octet-stream";
    }

    private static bool IsPlainText(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return false;
        }
        foreach (byte value in data)
        {
            if (value <= 0x08 || (value >= 0x0B && value <= 0x0C) || (value >= 0x0E && value <= 0x1B) || value == 0x7F)
            {
                return false;
            }
        }
        return true;
    }
}
