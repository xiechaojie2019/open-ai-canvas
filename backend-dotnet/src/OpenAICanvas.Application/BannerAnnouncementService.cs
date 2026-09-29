#nullable enable
using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence.Repositories;

namespace OpenAICanvas.Application;

/// <summary>创建/更新常驻通知请求。对应 Go: <c>app.CreateBannerAnnouncementRequest</c>（Update 为别名）。</summary>
public sealed class CreateBannerAnnouncementRequest
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("titleRuns")]
    public List<BannerTitleRun>? TitleRuns { get; set; }

    [JsonPropertyName("noticeType")]
    public string NoticeType { get; set; } = "";

    [JsonPropertyName("link")]
    public string Link { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("startsAt")]
    public DateTime? StartsAt { get; set; }

    [JsonPropertyName("endsAt")]
    public DateTime? EndsAt { get; set; }
}

/// <summary>管理端分页。对应 Go: <c>app.BannerAnnouncementPage</c>（struct 字段序）。</summary>
public sealed class BannerAnnouncementPage
{
    [JsonPropertyName("banners")]
    public List<BannerAnnouncement> Banners { get; set; } = [];

    [JsonPropertyName("total")]
    public long Total { get; set; }

    [JsonPropertyName("page")]
    public int Page { get; set; }

    [JsonPropertyName("pageSize")]
    public int Limit { get; set; }
}

/// <summary>
/// 首页常驻滚动通知。对应 Go: <c>app/announcement.go</c> 的 banner 部分。
/// 标题样式分段的字号/字重/字体/颜色走白名单，避免把任意 CSS 写进通知条。
/// </summary>
public sealed class BannerAnnouncementService
{
    private const int TitleMinFontSize = 10;
    private const int TitleMaxFontSize = 20;
    private const int TitleMaxRuns = 60;
    private const int TitleMaxTextRunes = 120;

    private static readonly long[] TitleFontWeights = [400, 500, 600, 700];
    private static readonly string[] TitleFontFamilies = ["sans", "serif", "mono"];
    private static readonly string[] NoticeTypes = ["notice", "activity", "update", "warning"];

    private readonly Repository _repository;

    public BannerAnnouncementService(Repository repository) => _repository = repository;

    /// <summary>生效通知（公开，无需登录）。对应 Go: <c>ActiveBannerAnnouncements</c>。</summary>
    public async Task<List<BannerAnnouncement>> ActiveAsync(CancellationToken cancellationToken = default)
    {
        List<BannerAnnouncement> banners = await _repository.ActiveBannerAnnouncementsAsync(
            DateTime.UtcNow, cancellationToken).ConfigureAwait(false);
        return banners;
    }

    /// <summary>管理端分页。对应 Go: <c>AdminBannerAnnouncementPage</c>。</summary>
    public async Task<BannerAnnouncementPage> AdminPageAsync(
        User? actor, string keyword, string status, int page, int limit,
        CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);
        (page, limit) = NormalizePage(page, limit);
        (List<BannerAnnouncement> banners, long total) = await _repository.AdminBannerAnnouncementsAsync(
            keyword, status, limit, (page - 1) * limit, cancellationToken).ConfigureAwait(false);
        return new BannerAnnouncementPage { Banners = banners, Total = total, Page = page, Limit = limit };
    }

    /// <summary>创建通知。对应 Go: <c>CreateBannerAnnouncement</c>。</summary>
    public async Task<BannerAnnouncement> CreateAsync(
        User? actor, CreateBannerAnnouncementRequest request, CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);
        (string title, List<BannerTitleRun>? titleRuns) = NormalizeTitle(request.Title, request.TitleRuns);
        string link = NormalizeLink(request.Link);
        string noticeType = NormalizeNoticeType(request.NoticeType);
        string status = ValidateStatus(request.Status);
        ValidateWindow(request);
        DateTime now = DateTime.UtcNow;
        BannerAnnouncement banner = new()
        {
            ID = IdGenerator.NewId(),
            Title = title,
            TitleRuns = titleRuns,
            NoticeType = noticeType,
            Link = link,
            Status = status,
            StartsAt = request.StartsAt,
            EndsAt = request.EndsAt,
            CreatedBy = actor!.ID,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await _repository.CreateBannerAnnouncementAsync(banner, cancellationToken).ConfigureAwait(false);
        return banner;
    }

    /// <summary>更新通知。对应 Go: <c>UpdateBannerAnnouncement</c>。</summary>
    public async Task<BannerAnnouncement> UpdateAsync(
        User? actor, string id, CreateBannerAnnouncementRequest request,
        CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);
        BannerAnnouncement banner = await _repository.BannerAnnouncementAsync(id.Trim(), cancellationToken)
            .ConfigureAwait(false)
            ?? throw AppError.BadAuthRequest("常驻通知不存在");
        (string title, List<BannerTitleRun>? titleRuns) = NormalizeTitle(request.Title, request.TitleRuns);
        string link = NormalizeLink(request.Link);
        string noticeType = NormalizeNoticeType(request.NoticeType);
        string status = ValidateStatus(request.Status);
        ValidateWindow(request);
        banner.Title = title;
        banner.TitleRuns = titleRuns;
        banner.NoticeType = noticeType;
        banner.Link = link;
        banner.Status = status;
        banner.StartsAt = request.StartsAt;
        banner.EndsAt = request.EndsAt;
        banner.UpdatedAt = DateTime.UtcNow;
        await _repository.UpdateBannerAnnouncementAsync(banner, cancellationToken).ConfigureAwait(false);
        return banner;
    }

    /// <summary>删除通知。对应 Go: <c>DeleteBannerAnnouncement</c>。</summary>
    public async Task DeleteAsync(User? actor, string id, CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);
        _ = await _repository.BannerAnnouncementAsync(id.Trim(), cancellationToken).ConfigureAwait(false)
            ?? throw AppError.BadAuthRequest("常驻通知不存在");
        await _repository.DeleteBannerAnnouncementAsync(id.Trim(), cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------- 校验

    private static (string Title, List<BannerTitleRun>? Runs) NormalizeTitle(
        string title, List<BannerTitleRun>? runs)
    {
        List<BannerTitleRun>? normalized = NormalizeRuns(runs);
        if (normalized is not { Count: > 0 })
        {
            string plain = title.Trim();
            if (plain.Length == 0)
            {
                throw AppError.BadAuthRequest("请填写通知标题");
            }
            if (Runes(plain) > TitleMaxTextRunes)
            {
                throw AppError.BadAuthRequest("通知标题不能超过 120 个字符");
            }
            return (plain, null);
        }
        string composed = RunsText(normalized).Trim();
        if (composed.Length == 0)
        {
            throw AppError.BadAuthRequest("请填写通知标题");
        }
        if (Runes(composed) > TitleMaxTextRunes)
        {
            throw AppError.BadAuthRequest("通知标题不能超过 120 个字符");
        }
        return (composed, normalized);
    }

    private static List<BannerTitleRun>? NormalizeRuns(List<BannerTitleRun>? runs)
    {
        if (runs is not { Count: > 0 })
        {
            return null;
        }
        if (runs.Count > TitleMaxRuns)
        {
            throw AppError.BadAuthRequest("通知标题样式片段过多");
        }
        List<BannerTitleRun> normalized = new(runs.Count);
        foreach (BannerTitleRun run in runs)
        {
            if (run.FontSize is not null
                && (run.FontSize < TitleMinFontSize || run.FontSize > TitleMaxFontSize))
            {
                throw AppError.BadAuthRequest("标题字号需在 10 到 20 之间");
            }
            if (run.FontWeight is not null && !TitleFontWeights.Contains(run.FontWeight.Value))
            {
                throw AppError.BadAuthRequest("标题字重仅支持 400 / 500 / 600 / 700");
            }
            string family = run.FontFamily.Trim();
            if (family.Length > 0 && !TitleFontFamilies.Contains(family))
            {
                throw AppError.BadAuthRequest("标题字体仅支持默认、衬线或等宽");
            }
            string color = run.Color.Trim().ToUpperInvariant();
            if (color.Length > 0 && !IsHexColor(color))
            {
                throw AppError.BadAuthRequest("标题颜色需为 #RRGGBB 格式");
            }
            if (run.Text.Length == 0)
            {
                continue;
            }
            normalized.Add(new BannerTitleRun
            {
                Text = run.Text,
                FontSize = run.FontSize,
                FontWeight = run.FontWeight,
                FontFamily = family,
                Color = color,
            });
        }
        return MergeRuns(normalized);
    }

    /// <summary>合并相邻同样式分段，避免反复编辑后存下大量零碎片段。</summary>
    private static List<BannerTitleRun> MergeRuns(List<BannerTitleRun> runs)
    {
        List<BannerTitleRun> merged = new(runs.Count);
        foreach (BannerTitleRun run in runs)
        {
            if (merged.Count > 0 && SameStyle(merged[^1], run))
            {
                merged[^1].Text += run.Text;
                continue;
            }
            merged.Add(run);
        }
        return merged;
    }

    private static bool SameStyle(BannerTitleRun left, BannerTitleRun right) =>
        Nullable.Equals(left.FontSize, right.FontSize)
        && Nullable.Equals(left.FontWeight, right.FontWeight)
        && left.FontFamily == right.FontFamily
        && left.Color == right.Color;

    private static bool IsHexColor(string value)
    {
        if (value.Length != 7 || value[0] != '#')
        {
            return false;
        }
        foreach (char character in value[1..])
        {
            if (character is >= '0' and <= '9' or >= 'A' and <= 'F')
            {
                continue;
            }
            return false;
        }
        return true;
    }

    private static string RunsText(List<BannerTitleRun> runs) =>
        string.Concat(runs.Select(run => run.Text));

    private static string NormalizeLink(string link)
    {
        link = link.Trim();
        if (link.Length == 0)
        {
            return "";
        }
        string lower = link.ToLowerInvariant();
        if (lower.StartsWith("http://", StringComparison.Ordinal)
            || lower.StartsWith("https://", StringComparison.Ordinal))
        {
            if (Runes(link) > 500)
            {
                throw AppError.BadAuthRequest("跳转链接不能超过 500 个字符");
            }
            return link;
        }
        if (link.StartsWith('/'))
        {
            if (Runes(link) > 500)
            {
                throw AppError.BadAuthRequest("站内路径不能超过 500 个字符");
            }
            return link;
        }
        throw AppError.BadAuthRequest("跳转链接仅支持 http(s) 外链或以 / 开头的站内路径");
    }

    private static string NormalizeNoticeType(string value)
    {
        string normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length == 0)
        {
            return "notice";
        }
        if (!NoticeTypes.Contains(normalized))
        {
            throw AppError.BadAuthRequest("通知类型无效，仅支持公告、活动、更新或警告");
        }
        return normalized;
    }

    private static string ValidateStatus(string status) =>
        status.Trim() is "active" or "disabled"
            ? status.Trim()
            : throw AppError.BadAuthRequest("通知状态无效，仅支持 active 或 disabled");

    private static void ValidateWindow(CreateBannerAnnouncementRequest request)
    {
        if (request.StartsAt is not null && request.EndsAt is not null
            && request.EndsAt.Value < request.StartsAt.Value)
        {
            throw AppError.BadAuthRequest("有效期结束时间不能早于开始时间");
        }
    }

    private static (int Page, int Limit) NormalizePage(int page, int limit)
    {
        if (page <= 0)
        {
            page = 1;
        }
        if (limit <= 0 || limit > 100)
        {
            limit = 20;
        }
        return (page, limit);
    }

    private static int Runes(string value) => value.EnumerateRunes().Count();
}
