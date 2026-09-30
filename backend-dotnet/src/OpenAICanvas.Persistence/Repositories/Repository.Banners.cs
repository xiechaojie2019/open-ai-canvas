using System.Data.Common;
using System.Text.Json;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Serialization;

namespace OpenAICanvas.Persistence.Repositories;

/// <summary>
/// 首页常驻滚动通知仓储。对应 Go: <c>repository/announcement.go</c> 的 banner 部分。
/// 样式分段持久化在 <c>title_runs</c> 文本列，由仓储层显式编解码。
/// </summary>
public sealed partial class Repository
{
    /// <summary>生效中的通知（时间窗内、active）。对应 Go: <c>ActiveBannerAnnouncements</c>。</summary>
    public async Task<List<BannerAnnouncement>> ActiveBannerAnnouncementsAsync(
        DateTime now, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        List<BannerAnnouncement> banners = (await QueryAsync<BannerAnnouncement>(
            connection,
            SqlBuilder.Select<BannerAnnouncement>(
                $"{Quote("status")} = @status AND ({Quote("startsAt")} IS NULL OR {Quote("startsAt")} <= @now) " +
                $"AND ({Quote("endsAt")} IS NULL OR {Quote("endsAt")} >= @now)",
                orderBy: Quote("createdAt") + " DESC"),
            new { status = "active", now },
            cancellationToken: cancellationToken).ConfigureAwait(false)).ToList();
        banners.ForEach(DecodeTitleRuns);
        return banners;
    }

    /// <summary>管理端分页。对应 Go: <c>AdminBannerAnnouncements</c>。</summary>
    public async Task<(List<BannerAnnouncement> Banners, long Total)> AdminBannerAnnouncementsAsync(
        string keyword, string status, int limit, int offset, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        string where = "1 = 1";
        Dictionary<string, object?> parameters = new(StringComparer.Ordinal);
        if (keyword.Trim().Length > 0)
        {
            where += $" AND {Quote("title")} LIKE @keyword";
            parameters["keyword"] = "%" + keyword.Trim() + "%";
        }
        if (status is "active" or "disabled")
        {
            where += $" AND {Quote("status")} = @status";
            parameters["status"] = status;
        }
        long? total = await ScalarAsync<long?>(
            connection,
            $"SELECT COUNT(*) FROM {Quote("bannerAnnouncements")} WHERE {where}",
            parameters,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        parameters["limit"] = limit;
        parameters["offset"] = offset;
        List<BannerAnnouncement> banners = (await QueryAsync<BannerAnnouncement>(
            connection,
            SqlBuilder.Select<BannerAnnouncement>(
                where, orderBy: Quote("createdAt") + " DESC", limitOffset: " LIMIT @limit OFFSET @offset"),
            parameters,
            cancellationToken: cancellationToken).ConfigureAwait(false)).ToList();
        banners.ForEach(DecodeTitleRuns);
        return (banners, total ?? 0);
    }

    /// <summary>按 ID 取通知。对应 Go: <c>BannerAnnouncement</c>（First，找不到返回 null）。</summary>
    public async Task<BannerAnnouncement?> BannerAnnouncementAsync(
        string id, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        BannerAnnouncement? banner = await FirstOrDefaultAsync<BannerAnnouncement>(
            connection,
            SqlBuilder.Select<BannerAnnouncement>($"{Quote("id")} = @id", limitOffset: " LIMIT 1"),
            new { id },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (banner is not null)
        {
            DecodeTitleRuns(banner);
        }
        return banner;
    }

    /// <summary>创建通知。对应 Go: <c>CreateBannerAnnouncement</c>。</summary>
    public async Task CreateBannerAnnouncementAsync(
        BannerAnnouncement banner, CancellationToken cancellationToken = default)
    {
        banner.TitleRunsJSON = EncodeTitleRuns(banner.TitleRuns);
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            SqlBuilder.Insert(typeof(BannerAnnouncement)),
            SqlBuilder.Parameters(banner),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>GORM Save 语义更新。对应 Go: <c>UpdateBannerAnnouncement</c>。</summary>
    public async Task UpdateBannerAnnouncementAsync(
        BannerAnnouncement banner, CancellationToken cancellationToken = default)
    {
        banner.TitleRunsJSON = EncodeTitleRuns(banner.TitleRuns);
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        int affected = await ExecuteAsync(
            connection,
            SqlBuilder.Update(typeof(BannerAnnouncement)),
            SqlBuilder.Parameters(banner),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (affected == 0)
        {
            await ExecuteAsync(
                connection,
                SqlBuilder.Insert(typeof(BannerAnnouncement)),
                SqlBuilder.Parameters(banner),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>删除通知。对应 Go: <c>DeleteBannerAnnouncement</c>。</summary>
    public async Task DeleteBannerAnnouncementAsync(
        string id, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            SqlBuilder.Delete(typeof(BannerAnnouncement), $"{Quote("id")} = @id"),
            new { id },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>分段编码为 title_runs 列；空分段写空串，与「纯文本标题」区分。</summary>
    private static string EncodeTitleRuns(List<BannerTitleRun>? runs)
    {
        if (runs is not { Count: > 0 })
        {
            return "";
        }
        return JsonSerializer.Serialize(runs, GoJson.WriteOptions);
    }

    private static void DecodeTitleRuns(BannerAnnouncement banner)
    {
        banner.TitleRuns = TitleRunsFromJson(banner.TitleRunsJSON);
    }

    /// <summary>损坏内容返回 null，降级为纯文本标题，不阻断通知条展示。</summary>
    private static List<BannerTitleRun>? TitleRunsFromJson(string raw)
    {
        raw = raw.Trim();
        if (raw.Length == 0)
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<List<BannerTitleRun>>(raw, GoJson.ReadOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
