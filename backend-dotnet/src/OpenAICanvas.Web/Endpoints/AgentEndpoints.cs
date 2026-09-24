#nullable enable
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using System.Text.Json.Serialization;
using OpenAICanvas.Application;
using OpenAICanvas.Application.CloudAgent;
using OpenAICanvas.Domain.Canvas.Capability;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Domain.Serialization;
using OpenAICanvas.Platform;
using OpenAICanvas.Web.Http;
using OpenAICanvas.Web.Security;
using OpenAICanvas.Web.Serialization;

namespace OpenAICanvas.Web.Endpoints;

/// <summary>
/// 云 Agent 路由。对应 Go: <c>handler/agent.go</c>。
/// 运行编排是持久的：浏览器事件流从不驱动执行。
/// </summary>
public static class AgentEndpoints
{
    public static void MapAgentRoutes(
        this IEndpointRouteBuilder api,
        CanvasService service,
        AgentProfileService profiles,
        CloudAgentSessionService sessions,
        CloudAgentRuntimeService runtime,
        IRateLimiter rateLimiter,
        IRuntimePolicyProvider policyProvider)
    {
        api.MapGet("/agent/capabilities", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
            (string version, string hash, string[] nodes) = CloudAgentNodes.CapabilitySetInfo();
            return ApiResults.Ok(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["version"] = 2,
                ["permissionModes"] = new[] { "read_only", "request_approval", "auto" },
                ["contextScopes"] = new[] { "canvas" },
                ["skills"] = true,
                ["writeTools"] = true,
                ["billing"] = "fixed_request",
                ["maxHistoryPairs"] = 0,
                ["maxHistoryBytes"] = 64000,
                ["maxSteps"] = 0,
                ["tools"] = CloudAgentTools.SupportedToolNames(),
                ["capabilitySetVersion"] = version,
                ["capabilitySetHash"] = hash,
                ["nodeTypes"] = nodes,
            });
        });

        api.MapGet("/agent/profile", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                string scope = context.Request.Query["scope"].ToString().Trim();
                string projectId = context.Request.Query["projectId"].ToString().Trim();
                string canvasId = context.Request.Query["canvasId"].ToString().Trim();
                if (scope.Length == 0)
                {
                    scope = canvasId.Length > 0 ? "canvas" : projectId.Length > 0 ? "project" : "user";
                }
                switch (scope)
                {
                    case "user" when projectId.Length > 0 || canvasId.Length > 0:
                        return ApiResults.Fail(
                            StatusCodes.Status400BadRequest,
                            new InvalidOperationException("用户偏好不能带项目或画布 ID"));
                    case "project" when projectId.Length == 0 || canvasId.Length > 0:
                        return ApiResults.Fail(
                            StatusCodes.Status400BadRequest,
                            new InvalidOperationException("项目偏好需要 projectId，且不能带 canvasId"));
                    case "canvas" when canvasId.Length == 0:
                        return ApiResults.Fail(
                            StatusCodes.Status400BadRequest,
                            new InvalidOperationException("画布偏好需要 canvasId"));
                    case not ("user" or "project" or "canvas"):
                        return ApiResults.Fail(
                            StatusCodes.Status400BadRequest,
                            new InvalidOperationException("无效的 Agent 偏好作用域"));
                }
                AgentProfileViewDto view = await profiles.ProfileForScopeAsync(
                    user.ID, projectId, canvasId, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(view);
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPatch("/agent/profile", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                AgentProfileRequest? request = await ReadSingleObjectAsync<AgentProfileRequest>(
                    context, 64 << 10, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(
                        StatusCodes.Status400BadRequest,
                        new InvalidOperationException("请求必须只包含一个 JSON 对象"));
                }
                AgentProfileViewDto view = await profiles.UpdateAsync(
                    user.ID, request, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(view);
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        // 每条追加消息开启一个不可变轮次并返回其 ID。
        api.MapPost("/agent/runs", async (HttpContext context) =>
        {
            return await CreateHandlerAsync(
                context, service, sessions, rateLimiter, policyProvider, parentID: "").ConfigureAwait(false);
        });
        api.MapPost("/agent/runs/{id}/messages", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            return await CreateHandlerAsync(
                context, service, sessions, rateLimiter, policyProvider,
                parentID: (string?)context.Request.RouteValues["id"] ?? "").ConfigureAwait(false);
        });

        api.MapGet("/agent/runs/{id}", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                string id = (string?)context.Request.RouteValues["id"] ?? "";
                CloudAgentRunDto run = await sessions.GetAsync(user.ID, id, cancellationToken)
                    .ConfigureAwait(false);
                return ApiResults.Ok(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["run"] = JsonSerializer.SerializeToElement(run, GoJson.WriteOptions),
                });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPost("/agent/runs/{id}/cancel", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                string id = (string?)context.Request.RouteValues["id"] ?? "";
                await runtime.CancelAsync(user.ID, id, cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["accepted"] = true,
                });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPost("/agent/runs/{id}/undo", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                UndoRequest? request = await ReadSingleObjectAsync<UndoRequest>(
                    context, 4096, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(
                        StatusCodes.Status400BadRequest,
                        new InvalidOperationException("请求必须只包含一个 JSON 对象"));
                }
                if (request.StepID.Length > 0
                    && (request.StepID.Trim() != request.StepID
                        || request.StepID.EnumerateRunes().Count() > 160))
                {
                    return ApiResults.Fail(
                        StatusCodes.Status400BadRequest,
                        new InvalidOperationException("stepId 无效"));
                }
                if (request.Reason.Length > 0
                    && (request.Reason.EnumerateRunes().Count() > 2000
                        || request.Reason.IndexOfAny(new[] { '\x00', '\r', '\n' }) >= 0))
                {
                    return ApiResults.Fail(
                        StatusCodes.Status400BadRequest,
                        new InvalidOperationException("reason 无效"));
                }
                string id = (string?)context.Request.RouteValues["id"] ?? "";
                Dictionary<string, JsonElement> result = await runtime.UndoAsync(
                    user.ID, id, request.StepID, request.ExpectedSnapshotHash, request.Reason,
                    cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(result);
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapPost("/agent/runs/{id}/approvals/{approvalId}/decision",
            async (HttpContext context, CancellationToken cancellationToken) =>
        {
            try
            {
                User user = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                string id = (string?)context.Request.RouteValues["id"] ?? "";
                string approvalId = (string?)context.Request.RouteValues["approvalId"] ?? "";
                await sessions.GetAsync(user.ID, id, cancellationToken).ConfigureAwait(false);
                DecisionRequest? request = await ReadSingleObjectAsync<DecisionRequest>(
                    context, 4096, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return ApiResults.Fail(
                        StatusCodes.Status400BadRequest,
                        new InvalidOperationException("请求必须只包含一个 JSON 对象"));
                }
                if (request.Decision is not ("approve" or "reject"))
                {
                    return ApiResults.Fail(
                        StatusCodes.Status400BadRequest,
                        new InvalidOperationException("decision 无效"));
                }
                if (request.Reason.Length > 0
                    && (request.Reason.EnumerateRunes().Count() > 2000
                        || request.Reason.IndexOfAny(new[] { '\x00', '\r', '\n' }) >= 0))
                {
                    return ApiResults.Fail(
                        StatusCodes.Status400BadRequest,
                        new InvalidOperationException("reason 无效"));
                }
                await runtime.DecideAsync(
                    user.ID, id, approvalId, request.Decision, request.Reason, request.MediaSettings,
                    cancellationToken).ConfigureAwait(false);
                return ApiResults.Ok(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["accepted"] = true,
                });
            }
            catch (Exception error)
            {
                return ApiResults.FailService(error, context);
            }
        });

        api.MapGet("/agent/runs/{id}/events", async (HttpContext context) =>
        {
            return await EventsHandlerAsync(context, service, sessions, policyProvider).ConfigureAwait(false);
        });
    }

    // ------------------------------------------------------------ 创建运行

    private static async Task<IResult> CreateHandlerAsync(
        HttpContext context,
        CanvasService service,
        CloudAgentSessionService sessions,
        IRateLimiter rateLimiter,
        IRuntimePolicyProvider policyProvider,
        string parentID)
    {
        try
        {
            User user = await service.CurrentUserAsync(SessionCookie.Read(context), context.RequestAborted)
                .ConfigureAwait(false);
            RuntimePolicySetting policy = policyProvider.Current();
            if (!await AuthEndpoints.EnforceRateLimitAsync(
                    context, rateLimiter, "tasks:" + user.ID,
                    policy.Request.TaskCreatePerMinute, TimeSpan.FromMinutes(1)).ConfigureAwait(false))
            {
                return Results.Empty;
            }
            CloudAgentRequestDto? request = await ReadSingleObjectAsync<CloudAgentRequestDto>(
                context, 128 << 10, context.RequestAborted).ConfigureAwait(false);
            if (request is null)
            {
                return ApiResults.Fail(
                    StatusCodes.Status400BadRequest,
                    new InvalidOperationException("请求必须只包含一个 JSON 对象"));
            }
            CloudAgentRunDto run = await sessions.CreateAsync(
                user.ID, request, parentID, context.RequestAborted).ConfigureAwait(false);
            return ApiResults.Ok(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["run"] = JsonSerializer.SerializeToElement(run, GoJson.WriteOptions),
            });
        }
        catch (Exception error)
        {
            return ApiResults.FailService(error, context);
        }
    }

    // ------------------------------------------------------------ SSE 事件流

    private static async Task<IResult> EventsHandlerAsync(
        HttpContext context,
        CanvasService service,
        CloudAgentSessionService sessions,
        IRuntimePolicyProvider policyProvider)
    {
        CancellationToken cancellationToken = context.RequestAborted;
        try
        {
            _ = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception error)
        {
            IResult failure = ApiResults.FailService(error, context);
            await failure.ExecuteAsync(context).ConfigureAwait(false);
            return Results.Empty;
        }
        string id = (string?)context.Request.RouteValues["id"] ?? "";
        long after;
        {
            long cursor = 0;
            bool invalid = false;
            foreach (string raw in new[]
                     {
                         context.Request.Query["after"].ToString(),
                         context.Request.Headers["Last-Event-ID"].ToString(),
                     })
            {
                if (string.IsNullOrEmpty(raw))
                {
                    continue;
                }
                if (!long.TryParse(raw, out long value) || value < 0)
                {
                    invalid = true;
                    break;
                }
                cursor = Math.Max(cursor, value);
            }
            if (invalid)
            {
                IResult failure = ApiResults.Fail(
                    StatusCodes.Status400BadRequest,
                    new InvalidOperationException("无效的事件游标"));
                await failure.ExecuteAsync(context).ConfigureAwait(false);
                return Results.Empty;
            }
            after = cursor;
        }
        CloudAgentRunDto? run;
        try
        {
            run = await sessions.GetAsync(id.Length > 0 ? (await service.CurrentUserAsync(
                SessionCookie.Read(context), cancellationToken).ConfigureAwait(false)).ID : "",
                id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            IResult failure = ApiResults.FailService(error, context);
            await failure.ExecuteAsync(context).ConfigureAwait(false);
            return Results.Empty;
        }
        User streamUser = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
            .ConfigureAwait(false);

        context.Response.Headers["Content-Type"] = "text/event-stream";
        context.Response.Headers["Cache-Control"] = "no-cache";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        try
        {
            long revision = run.Revision;
            DateTime lastWrite = DateTime.UtcNow;
            while (true)
            {
                if (run is not null)
                {
                    foreach (CloudAgentEventDto agentEvent in run.Events ?? [])
                    {
                        if (agentEvent.Seq > after)
                        {
                            await WriteAgentSseAsync(
                                context, "agent_event", agentEvent.Seq, agentEvent, cancellationToken)
                                .ConfigureAwait(false);
                            after = agentEvent.Seq;
                        }
                    }
                    // 快照是状态观察而非可回放事件：不带 id，不推进游标。
                    CloudAgentRunDto snapshot = JsonSerializer.Deserialize<CloudAgentRunDto>(
                        JsonSerializer.Serialize(run, GoJson.WriteOptions))!;
                    snapshot.Events = null;
                    await WriteAgentSseAsync(context, "run_snapshot", 0, snapshot, cancellationToken)
                        .ConfigureAwait(false);
                    revision = run.Revision;
                    lastWrite = DateTime.UtcNow;
                    if (!run.CleanupPending
                        && run.Status is "completed" or "failed" or "cancelled" or "rejected")
                    {
                        break;
                    }
                }
                else if (DateTime.UtcNow - lastWrite >= TimeSpan.FromSeconds(15))
                {
                    await context.Response.Body.WriteAsync(": heartbeat\n\n"u8.ToArray(), cancellationToken)
                        .ConfigureAwait(false);
                    await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
                    lastWrite = DateTime.UtcNow;
                }
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                User pollUser = await service.CurrentUserAsync(SessionCookie.Read(context), cancellationToken)
                    .ConfigureAwait(false);
                _ = pollUser;
                run = await sessions.GetIfChangedAsync(streamUser.ID, id, revision, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 客户端断开：正常退出。
        }
        return Results.Empty;
    }

    private static async Task WriteAgentSseAsync(
        HttpContext context, string eventName, long id, object value, CancellationToken cancellationToken)
    {
        string data = JsonSerializer.Serialize(value, GoJson.WriteOptions);
        StringBuilder builder = new();
        if (id > 0)
        {
            builder.Append("id: ").Append(id).Append('\n');
        }
        builder.Append("event: ").Append(eventName).Append('\n');
        builder.Append("data: ").Append(data).Append("\n\n");
        await context.Response.Body.WriteAsync(
            Encoding.UTF8.GetBytes(builder.ToString()), cancellationToken).ConfigureAwait(false);
        await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------ 请求体

    private sealed class UndoRequest
    {
        [JsonPropertyName("stepId")]
        public string StepID { get; set; } = "";

        [JsonPropertyName("expectedSnapshotHash")]
        public string ExpectedSnapshotHash { get; set; } = "";

        [JsonPropertyName("reason")]
        public string Reason { get; set; } = "";
    }

    private sealed class DecisionRequest
    {
        [JsonPropertyName("decision")]
        public string Decision { get; set; } = "";

        [JsonPropertyName("reason")]
        public string Reason { get; set; } = "";

        [JsonPropertyName("mediaSettings")]
        public CloudAgentMediaSettings? MediaSettings { get; set; }
    }

    /// <summary>对应 Go 的 DisallowUnknownFields + 单对象校验：拒绝重复 JSON 或尾部内容。</summary>
    private static async Task<T?> ReadSingleObjectAsync<T>(
        HttpContext context,
        long maxBytes,
        CancellationToken cancellationToken)
        where T : class
    {
        if (maxBytes > 0 && context.Request.ContentLength is > 0 && context.Request.ContentLength > maxBytes)
        {
            return null;
        }
        try
        {
            string body;
            using (StreamReader reader = new(context.Request.Body, Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true))
            {
                body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            }
            JsonSerializerOptions options = new(GoJson.ReadOptions)
            {
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            };
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            return JsonSerializer.Deserialize<T>(body, options);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (BadHttpRequestException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
