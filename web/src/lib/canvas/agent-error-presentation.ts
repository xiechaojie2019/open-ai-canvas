import { ApiError } from "@/services/api/request";
import { AgentStreamError } from "@/services/api/agent";

export function agentErrorPresentation(cause: unknown, fallback = "Agent 执行失败") {
    const text = typeof cause === "string" ? cause.trim() : cause instanceof Error ? cause.message : fallback;
    if ((cause instanceof ApiError || cause instanceof AgentStreamError) && cause.status === 404) {
        return {
            title: "Agent 接口或资源不存在",
            text: text || "当前后端未找到请求的 Agent 接口或会话资源。",
            meta: "请确认后端已加载 Agent 路由，以及画布、会话和模型仍可访问；旧进程需要更新后重启。",
        };
    }
    if (cause instanceof ApiError && cause.status === 501) {
        return {
            title: "当前 Agent 能力尚未开放",
            text: text || "当前请求的 Agent 能力尚未开放，请使用 capabilities 返回的权限和工具范围。",
        };
    }
    // 对话上下文超限：唯一「必须新建对话才能继续」的可操作错误，必须给明确指引。
    // 只显示通用的「Agent 执行失败 / 请求已被服务端拒绝」时，用户会误以为是模型或操作出错
    //（线上就发生过）。后端文案与阈值见 CloudAgentSessionService.CreateAsync（>64000 直接 400）。
    if (isConversationContextLimit(cause)) {
        return {
            title: "对话上下文已超上限，请新建对话",
            text: text || "本轮对话积累的上下文已经超过 64KB 上限。",
            meta: "点击「新建对话」开一轮新会话后继续即可；画布上的节点和图片不会丢失。",
        };
    }
    // 渠道、鉴权、配额等业务错误保留真实语义，不能按 message 猜测为服务未部署。
    return { title: fallback, text: text || fallback };
}

export function agentSubmissionErrorTitle(cause: unknown, accepted: boolean, requestSent: boolean) {
    if (accepted) return "运行已接收，但本地提交记录清理失败";
    if (!requestSent) return "发送前检查失败，尚未提交 Agent 请求";
    // 与 agentErrorPresentation 保持一致：上下文超限要直接告诉用户「新建对话」，
    // 不要淹没在通用的「请求已被服务端拒绝」里。
    if (isConversationContextLimit(cause)) return "对话上下文已超上限，请新建对话";
    const status = cause instanceof ApiError ? cause.status : undefined;
    if (status && [400, 401, 403, 404, 422].includes(status)) return "请求已被服务端拒绝";
    if (status) return "服务端已返回错误；重试将核对原请求，不重复创建";
    return "未收到服务端确认；重试将核对原请求，不重复创建";
}

/** 后端「对话上下文超过 64KB，请新建对话」这类错误的识别（文案匹配，不依赖 status/code）。 */
function isConversationContextLimit(cause: unknown) {
    const text = typeof cause === "string" ? cause : cause instanceof Error ? cause.message : "";
    return text.includes("对话上下文超过") || text.includes("请新建对话");
}
