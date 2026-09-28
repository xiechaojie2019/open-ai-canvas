#nullable enable

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Outbound;

namespace OpenAICanvas.Application;

/// <summary>Eagle 素材库目录项。对应 Go: <c>app.EagleFolder</c>。</summary>
public sealed class EagleFolderDto
{
    [JsonPropertyName("id")] public string ID { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("parentId")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ParentID { get; init; }
}

/// <summary>Eagle 素材库信息。libraryPath 只用于服务端取文件，不能下发给浏览器。</summary>
public sealed class EagleLibraryDto
{
    [JsonPropertyName("applicationVersion")] public string ApplicationVersion { get; init; } = "";
    [JsonPropertyName("libraryName")] public string LibraryName { get; init; } = "";
    [JsonIgnore] public string LibraryPath { get; init; } = "";
    [JsonPropertyName("folders")] public List<EagleFolderDto> Folders { get; init; } = [];
}

/// <summary>Eagle 素材项。对应 Go: <c>app.EagleItem</c>。</summary>
public sealed class EagleItemDto
{
    [JsonPropertyName("id")] public string ID { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("size")] public long Size { get; init; }
    [JsonPropertyName("extension")] public string Extension { get; init; } = "";
    [JsonPropertyName("tags")] public List<string> Tags { get; init; } = [];
    [JsonPropertyName("folderIds")] public List<string> FolderIDs { get; init; } = [];
    [JsonPropertyName("url")] public string URL { get; init; } = "";
    [JsonPropertyName("annotation")] public string Annotation { get; init; } = "";
    [JsonPropertyName("modificationTime")] public long ModificationTime { get; init; }
    [JsonPropertyName("width")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int Width { get; init; }
    [JsonPropertyName("height")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int Height { get; init; }
    [JsonPropertyName("deleted")] public bool Deleted { get; init; }
}

/// <summary>Eagle 新建素材请求。对应 Go: <c>app.EagleAddItemRequest</c>。</summary>
public sealed class EagleAddItemRequestDto
{
    [JsonPropertyName("url")] public string URL { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("folderId")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? FolderID { get; set; }
    [JsonPropertyName("tags")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<string>? Tags { get; set; }
    [JsonPropertyName("annotation")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Annotation { get; set; }
    [JsonPropertyName("website")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Website { get; set; }
    [JsonPropertyName("modificationTime")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public long ModificationTime { get; set; }
}

public sealed class EagleCreatedItemDto
{
    [JsonPropertyName("id")] public string ID { get; init; } = "";
}

public sealed class EagleItemQueryDto
{
    public string FolderID { get; init; } = "";
    public string Keyword { get; init; } = "";
    public int Limit { get; init; } = 60;
    public int Offset { get; init; }
}

/// <summary>从 Eagle 素材库读取的文件，端点负责写出并释放 Body。</summary>
public sealed class EagleFileDownload : IAsyncDisposable
{
    public required string Name { get; init; }
    public required string MimeType { get; init; }
    public required long Length { get; init; }
    public required Stream Body { get; init; }

    public ValueTask DisposeAsync() => Body.DisposeAsync();
}

/// <summary>
/// Eagle 本机资产连接服务。
/// Eagle 不走通用公网 URL 校验：协议只允许 loopback、固定 41595 端口，并且客户端禁用代理与重定向。
/// </summary>
public sealed class EagleService
{
    private const string DefaultBaseURL = "http://127.0.0.1:41595";
    private const int EaglePort = 41595;
    private const long MaxJSONResponseBytes = 8L << 20;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<EagleLibraryDto> LibraryAsync(
        string? rawBaseURL, CancellationToken cancellationToken = default)
    {
        Uri baseURL = ValidateBaseURL(rawBaseURL);
        EagleEnvelope<EagleLibraryPayload> response = await JsonRequestAsync<EagleLibraryPayload>(
            HttpMethod.Get, baseURL, "/api/library/info", null, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(response.Status, "success", StringComparison.OrdinalIgnoreCase) || response.Data is null)
        {
            throw new InvalidOperationException("Eagle 未返回成功状态，请确认 Eagle 已启动并打开素材库");
        }

        return new EagleLibraryDto
        {
            ApplicationVersion = response.Data.ApplicationVersion ?? "",
            LibraryName = response.Data.Library?.Name ?? "",
            LibraryPath = response.Data.Library?.Path ?? "",
            Folders = FlattenFolders(response.Data.Folders ?? [], parentID: null),
        };
    }

    public async Task<List<EagleItemDto>> ItemsAsync(
        string? rawBaseURL, EagleItemQueryDto query, CancellationToken cancellationToken = default)
    {
        Uri baseURL = ValidateBaseURL(rawBaseURL);
        int limit = query.Limit is > 0 and <= 200 ? query.Limit : 60;
        int offset = Math.Max(0, query.Offset);
        List<string> parameters = ["limit=" + limit, "offset=" + offset];
        if (!string.IsNullOrWhiteSpace(query.FolderID))
        {
            parameters.Add("folders=" + Uri.EscapeDataString(query.FolderID.Trim()));
        }
        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            parameters.Add("keyword=" + Uri.EscapeDataString(query.Keyword.Trim()));
        }

        EagleEnvelope<List<EagleRawItem>> response = await JsonRequestAsync<List<EagleRawItem>>(
            HttpMethod.Get, baseURL, "/api/item/list?" + string.Join('&', parameters), null, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(response.Status, "success", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Eagle 未返回素材列表");
        }

        return (response.Data ?? []).Select(MapItem).ToList();
    }

    public async Task<EagleFileDownload> OpenItemFileAsync(
        string? rawBaseURL, string itemID, CancellationToken cancellationToken = default)
    {
        ValidateItemID(itemID);
        EagleLibraryDto library = await LibraryAsync(rawBaseURL, cancellationToken).ConfigureAwait(false);
        string thumbnailPath = await RequestThumbnailPathAsync(rawBaseURL, itemID, cancellationToken).ConfigureAwait(false);
        string originalPath = ResolveOriginalPath(thumbnailPath, itemID, library.LibraryPath);
        return await OpenLocalFileAsync(originalPath, cancellationToken).ConfigureAwait(false);
    }

    public async Task<EagleFileDownload> OpenItemThumbnailAsync(
        string? rawBaseURL, string itemID, CancellationToken cancellationToken = default)
    {
        ValidateItemID(itemID);
        EagleLibraryDto library = await LibraryAsync(rawBaseURL, cancellationToken).ConfigureAwait(false);
        string thumbnailPath = await RequestThumbnailPathAsync(rawBaseURL, itemID, cancellationToken).ConfigureAwait(false);
        string itemDirectory = ItemDirectory(library.LibraryPath, itemID);
        if (!IsWithinDirectory(itemDirectory, thumbnailPath) ||
            string.Equals(itemDirectory, thumbnailPath, GetPathComparison()))
        {
            throw new InvalidOperationException("Eagle 缩略图路径不在当前素材库内");
        }
        return await OpenLocalFileAsync(thumbnailPath, cancellationToken).ConfigureAwait(false);
    }

    public async Task<EagleCreatedItemDto> AddItemAsync(
        string? rawBaseURL, EagleAddItemRequestDto request, CancellationToken cancellationToken = default)
    {
        if (!IsMediaDataURL(request.URL))
        {
            throw AppError.BadAuthRequest("写入 Eagle 只接受图片、视频或音频数据");
        }
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw AppError.BadAuthRequest("写入 Eagle 时必须提供素材名称");
        }

        Uri baseURL = ValidateBaseURL(rawBaseURL);
        EagleEnvelope<EagleCreatedItemDto> response = await JsonRequestAsync<EagleCreatedItemDto>(
            HttpMethod.Post, baseURL, "/api/item/addFromURL", request, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(response.Status, "success", StringComparison.OrdinalIgnoreCase) || response.Data is null)
        {
            throw new InvalidOperationException("Eagle 拒绝写入素材");
        }
        return response.Data;
    }

    public async Task CreateFolderAsync(
        string? rawBaseURL, string name, string? parentID, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw AppError.BadAuthRequest("Eagle 文件夹名称不能为空");
        }
        Uri baseURL = ValidateBaseURL(rawBaseURL);
        EagleFolderCreateRequest request = new() { FolderName = name.Trim(), Parent = parentID?.Trim() };
        EagleEnvelope<object> response = await JsonRequestAsync<object>(
            HttpMethod.Post, baseURL, "/api/folder/create", request, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(response.Status, "success", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Eagle 文件夹创建失败");
        }
    }

    public static Uri ValidateBaseURL(string? rawBaseURL)
    {
        string value = string.IsNullOrWhiteSpace(rawBaseURL) ? DefaultBaseURL : rawBaseURL.Trim();
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? parsed)
            || !string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || parsed.UserInfo.Length != 0
            || parsed.Query.Length != 0
            || parsed.Fragment.Length != 0
            || (parsed.AbsolutePath is not ("" or "/")))
        {
            throw AppError.BadAuthRequest("Eagle 地址必须是 http://127.0.0.1:41595");
        }

        string host = parsed.Host.Trim('[', ']').ToLowerInvariant();
        if (host is not ("127.0.0.1" or "localhost" or "::1") || parsed.Port != EaglePort)
        {
            throw AppError.BadAuthRequest("Eagle 插件只允许连接本机地址的默认端口 41595");
        }
        return new UriBuilder(parsed) { Path = "", Query = "", Fragment = "" }.Uri;
    }

    private async Task<string> RequestThumbnailPathAsync(
        string? rawBaseURL, string itemID, CancellationToken cancellationToken)
    {
        Uri baseURL = ValidateBaseURL(rawBaseURL);
        EagleEnvelope<string> response = await JsonRequestAsync<string>(
            HttpMethod.Get, baseURL, "/api/item/thumbnail?id=" + Uri.EscapeDataString(itemID), null,
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(response.Status, "success", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(response.Data))
        {
            throw new InvalidOperationException("Eagle 未返回缩略图路径");
        }
        try
        {
            return Path.GetFullPath(Uri.UnescapeDataString(response.Data));
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException)
        {
            throw new InvalidOperationException("Eagle 缩略图路径编码无效", error);
        }
    }

    private static async Task<EagleFileDownload> OpenLocalFileAsync(
        string path, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        FileStream file;
        try
        {
            file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 64 * 1024, options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new InvalidOperationException("无法读取 Eagle 文件，请确认素材库仍处于可用状态", error);
        }

        try
        {
            FileInfo info = new(path);
            if ((info.Attributes & FileAttributes.Directory) != 0)
            {
                throw new InvalidOperationException("Eagle 文件路径指向目录");
            }
            string mimeType = MimeTypeFor(Path.GetExtension(path));
            return new EagleFileDownload
            {
                Name = Path.GetFileName(path),
                MimeType = mimeType,
                Length = info.Length,
                Body = file,
            };
        }
        catch
        {
            await file.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<EagleEnvelope<T>> JsonRequestAsync<T>(
        HttpMethod method, Uri baseURL, string endpoint, object? payload,
        CancellationToken cancellationToken)
    {
        if (!endpoint.StartsWith("/", StringComparison.Ordinal)
            || !Uri.TryCreate(endpoint, UriKind.Relative, out Uri? relative)
            || relative.IsAbsoluteUri || relative.Host.Length != 0)
        {
            throw new InvalidOperationException("Eagle API 路径无效");
        }

        using HttpClient client = OutboundHttpClient.Create(
            TimeSpan.FromMinutes(2), allowAutoRedirect: false, useProxy: false, allowLoopbackOnly: true);
        using HttpRequestMessage request = new(method, new Uri(baseURL, relative));
        if (payload is not null)
        {
            string json = JsonSerializer.Serialize(payload, JsonOptions);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw AppError.New(504, "Eagle API 响应超时，请稍后重试");
        }
        catch (HttpRequestException error)
        {
            throw new InvalidOperationException("无法连接 Eagle，请确认 Eagle 已启动并打开素材库", error);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    throw new InvalidOperationException("Eagle 未找到当前接口或素材，请确认 Eagle 已打开素材库并使用默认 API 地址");
                }
                throw new InvalidOperationException($"Eagle API 返回 HTTP {(int)response.StatusCode}");
            }

            if (response.Content.Headers.ContentLength is > MaxJSONResponseBytes)
            {
                throw new InvalidOperationException("Eagle API 响应超过大小上限");
            }

            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using MemoryStream buffer = new();
            byte[] chunk = new byte[64 * 1024];
            while (true)
            {
                int read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                if (buffer.Length + read > MaxJSONResponseBytes)
                {
                    throw new InvalidOperationException("Eagle API 响应超过大小上限");
                }
                await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            try
            {
                return JsonSerializer.Deserialize<EagleEnvelope<T>>(buffer.ToArray(), JsonOptions)
                    ?? throw new InvalidOperationException("Eagle API 返回了无法解析的数据");
            }
            catch (JsonException error)
            {
                throw new InvalidOperationException("Eagle API 返回了无法解析的数据", error);
            }
        }
    }

    private static void ValidateItemID(string itemID)
    {
        if (string.IsNullOrWhiteSpace(itemID) || itemID.Contains('/') || itemID.Contains('\\')
            || itemID.Contains('?') || itemID.Contains('&'))
        {
            throw AppError.BadAuthRequest("Eagle 素材 ID 无效");
        }
    }

    private static string ResolveOriginalPath(string thumbnailPath, string itemID, string libraryPath)
    {
        string itemDirectory = ItemDirectory(libraryPath, itemID);
        if (!IsWithinDirectory(itemDirectory, thumbnailPath)
            || string.Equals(itemDirectory, thumbnailPath, GetPathComparison()))
        {
            throw new InvalidOperationException("Eagle 素材路径不在当前素材库内");
        }

        string fileName = Path.GetFileName(thumbnailPath);
        string stem = Path.GetFileNameWithoutExtension(fileName);
        if (stem.EndsWith("_thumbnail", StringComparison.OrdinalIgnoreCase))
        {
            stem = stem[..^"_thumbnail".Length];
        }
        string extension = Path.GetExtension(fileName);
        if (extension.Length != 0)
        {
            string candidate = Path.Combine(itemDirectory, stem + extension);
            if (File.Exists(candidate) && IsWithinDirectory(itemDirectory, candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        string[] candidates;
        try
        {
            candidates = Directory.EnumerateFiles(itemDirectory)
                .Where(candidate => !string.Equals(Path.GetFileName(candidate), "metadata.json", StringComparison.OrdinalIgnoreCase)
                    && !Path.GetFileName(candidate).Contains("_thumbnail", StringComparison.OrdinalIgnoreCase))
                .OrderBy(candidate => candidate, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("Eagle 原文件目录不存在", error);
        }
        return candidates.FirstOrDefault(candidate => IsWithinDirectory(itemDirectory, candidate))
            ?? throw new InvalidOperationException("Eagle 原始文件不存在");
    }

    private static string ItemDirectory(string libraryPath, string itemID)
    {
        if (string.IsNullOrWhiteSpace(libraryPath) || !Path.IsPathFullyQualified(libraryPath))
        {
            throw new InvalidOperationException("Eagle 素材库路径无效，无法安全读取原文件");
        }
        return Path.GetFullPath(Path.Combine(libraryPath, "images", itemID + ".info"));
    }

    private static bool IsWithinDirectory(string directory, string path)
    {
        string relative;
        try
        {
            relative = Path.GetRelativePath(Path.GetFullPath(directory), Path.GetFullPath(path));
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException)
        {
            return false;
        }
        return relative != "." && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar)
            && !Path.IsPathRooted(relative);
    }

    private static StringComparison GetPathComparison() =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static List<EagleFolderDto> FlattenFolders(
        IReadOnlyList<EagleRawFolder> folders, string? parentID)
    {
        List<EagleFolderDto> result = [];
        foreach (EagleRawFolder folder in folders)
        {
            string id = folder.ID ?? "";
            result.Add(new EagleFolderDto { ID = id, Name = folder.Name ?? "", ParentID = parentID });
            result.AddRange(FlattenFolders(folder.Children ?? [], id));
        }
        return result;
    }

    private static EagleItemDto MapItem(EagleRawItem item) => new()
    {
        ID = item.ID ?? "",
        Name = item.Name ?? "",
        Size = item.Size,
        Extension = (item.Extension ?? "").Trim().TrimStart('.').ToLowerInvariant(),
        Tags = item.Tags ?? [],
        FolderIDs = item.FolderIDs ?? [],
        URL = item.URL ?? "",
        Annotation = item.Annotation ?? "",
        ModificationTime = item.ModificationTime,
        Width = item.Width,
        Height = item.Height,
        Deleted = item.Deleted,
    };

    private static bool IsMediaDataURL(string value)
    {
        string trimmed = value.Trim();
        if (!trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        int comma = trimmed.IndexOf(',');
        if (comma <= "data:".Length)
        {
            return false;
        }
        string mediaType = trimmed["data:".Length..comma];
        int semicolon = mediaType.IndexOf(';');
        if (semicolon >= 0)
        {
            mediaType = mediaType[..semicolon];
        }
        mediaType = mediaType.Trim().ToLowerInvariant();
        return mediaType.StartsWith("image/", StringComparison.Ordinal)
            || mediaType.StartsWith("video/", StringComparison.Ordinal)
            || mediaType.StartsWith("audio/", StringComparison.Ordinal);
    }

    private static string MimeTypeFor(string extension) => extension.ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".svg" => "image/svg+xml",
        ".mp4" => "video/mp4",
        ".webm" => "video/webm",
        ".mov" => "video/quicktime",
        ".mp3" => "audio/mpeg",
        ".wav" => "audio/wav",
        ".ogg" => "audio/ogg",
        _ => "application/octet-stream",
    };

    private sealed class EagleEnvelope<T>
    {
        [JsonPropertyName("status")] public string Status { get; init; } = "";
        [JsonPropertyName("data")] public T? Data { get; init; }
    }

    private sealed class EagleRawFolder
    {
        [JsonPropertyName("id")] public string? ID { get; init; }
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("children")] public List<EagleRawFolder>? Children { get; init; }
    }

    private sealed class EagleRawItem
    {
        [JsonPropertyName("id")] public string? ID { get; init; }
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("size")] public long Size { get; init; }
        [JsonPropertyName("ext")] public string? Extension { get; init; }
        [JsonPropertyName("tags")] public List<string>? Tags { get; init; }
        [JsonPropertyName("folders")] public List<string>? FolderIDs { get; init; }
        [JsonPropertyName("url")] public string? URL { get; init; }
        [JsonPropertyName("annotation")] public string? Annotation { get; init; }
        [JsonPropertyName("modificationTime")] public long ModificationTime { get; init; }
        [JsonPropertyName("width")] public int Width { get; init; }
        [JsonPropertyName("height")] public int Height { get; init; }
        [JsonPropertyName("isDeleted")] public bool Deleted { get; init; }
    }

    private sealed class EagleLibraryPayload
    {
        [JsonPropertyName("folders")] public List<EagleRawFolder>? Folders { get; init; }
        [JsonPropertyName("applicationVersion")] public string? ApplicationVersion { get; init; }
        [JsonPropertyName("library")] public EagleLibraryPath? Library { get; init; }
    }

    private sealed class EagleLibraryPath
    {
        [JsonPropertyName("path")] public string? Path { get; init; }
        [JsonPropertyName("name")] public string? Name { get; init; }
    }

    private sealed class EagleFolderCreateRequest
    {
        [JsonPropertyName("folderName")] public string FolderName { get; init; } = "";
        [JsonPropertyName("parent")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Parent { get; init; }
    }
}
