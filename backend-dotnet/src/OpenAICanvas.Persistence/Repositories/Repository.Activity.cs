#nullable enable
using System.Data.Common;
using Dapper;
using OpenAICanvas.Domain.Entities;

namespace OpenAICanvas.Persistence.Repositories;

/// <summary>用户每日活跃记录。对应 Go: <c>repository/analytics.go: RecordUserActivity</c>。</summary>
public sealed partial class Repository
{
    /// <summary>
    /// 记录用户活跃事件（按天聚合，upsert）。
    /// 未知事件类型静默忽略（与 Go 的 <c>default: return nil</c> 一致）。
    /// </summary>
    public async Task RecordUserActivityAsync(
        string userId, string @event, int count, DateTime now, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(userId))
        {
            return;
        }

        if (count <= 0)
        {
            count = 1;
        }

        DateTime utc = now.ToUniversalTime();
        DateTime day = new(utc.Year, utc.Month, utc.Day, 0, 0, 0, DateTimeKind.Utc);

        // 事件类型决定自增列；canvas 是布尔标记，其余是计数。
        // 未知事件直接返回，不落库（与 Go 的 default 分支一致）。
        (string column, bool isCounter) = @event switch
        {
            "login" => ("loginCount", true),
            "task" => ("taskCount", true),
            "agent_message" => ("agentMessageCount", true),
            "canvas" => ("canvasActive", false),
            "asset" => ("assetCount", true),
            "resource" => ("resourceCount", true),
            _ => ("", false),
        };

        if (column.Length == 0)
        {
            return;
        }

        UserDailyActivity activity = new()
        {
            ID = userId + ":" + day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            Day = day,
            UserID = userId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        // DO UPDATE 右侧列引用必须带表名：目标行与 excluded 行都含同名列，
        // 裸列名在 PostgreSQL 下报 column reference is ambiguous（生产实测）。
        // 与 Go 的 gorm.Expr("user_daily_activities.login_count + ?", count) 一致。
        string qualifiedColumn = "\"userDailyActivities\"." + Quote(column);
        string setClause = isCounter
            ? $"{Quote(column)} = {qualifiedColumn} + @count"
            : $"{Quote(column)} = @activeValue";

        // login 不计入活跃窗口（Go 只在非 login 事件里写 first/last_active_at）。
        if (@event != "login")
        {
            setClause += $",\n                \"firstActiveAt\" = COALESCE(\"userDailyActivities\".\"firstActiveAt\", @now),\n                \"lastActiveAt\" = @now";
        }

        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            $"""
            INSERT INTO "userDailyActivities"
                ("id", "day", "userId", "loginCount", "taskCount", "agentMessageCount",
                 "canvasActive", "assetCount", "resourceCount", "createdAt", "updatedAt")
            VALUES
                (@ID, @Day, @UserID, @initialLogin, 0, 0, @initialBoolean, 0, 0, @CreatedAt, @UpdatedAt)
            ON CONFLICT ("day", "userId") DO UPDATE SET
                {setClause},
                "updatedAt" = @now
            """,
            new
            {
                activity.ID,
                activity.Day,
                activity.UserID,
                activity.CreatedAt,
                activity.UpdatedAt,
                now,
                count,
                activeValue = true,
                initialBoolean = IsColumnBoolean(column),
                // 首插即事件：把 count 写进对应计数列（与 Go 首插实体字段一致），
                // 否则首次登录/操作的计数会被 VALUES 里的 0 吞掉。
                initialLogin = @event == "login" ? count : 0,
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private static bool IsColumnBoolean(string column) => column == "canvasActive";
}
