import { localForageStorageForScope } from "@/lib/localforage-storage";
import { markdownPlainText } from "@/lib/markdown-plain-text";
import { getActiveUserScope } from "@/lib/user-scope";
import { http } from "@/services/api/request";
import type { AgentPermissionMode, AgentRun } from "@/services/api/agent";

export type CloudAgentConversationMessage = {
    id: string;
    role: "user" | "assistant" | "system" | "tool" | "error";
    title?: string;
    text: string;
    meta?: string;
    detail?: unknown;
    attachments?: Array<{ id: string; name: string; url: string }>;
};

export type CloudAgentConversation = {
    id: string;
    title: string;
    messages: CloudAgentConversationMessage[];
    run: AgentRun | null;
    model?: string;
    permissionMode: AgentPermissionMode;
    skillIds?: string[];
    createdAt: string;
    updatedAt: string;
};

export type CloudAgentConversationDocument = {
    version: 1;
    activeId: string | null;
    conversations: CloudAgentConversation[];
};

export type CloudAgentPendingSubmission = {
    fingerprint: string;
    key: string;
    request?: import("@/services/api/agent").CreateAgentRunInput;
    parentRunId?: string;
    messageId?: string;
};

const CLOUD_AGENT_CONVERSATIONS_KEY = "cloud-agent-conversations-v1";

export async function loadCloudAgentConversations(canvasId: string): Promise<CloudAgentConversationDocument> {
    if (!canvasId) throw new Error("缺少画布 ID，无法读取 Agent 对话");
    const value = await localForageStorageForScope(getActiveUserScope()).getItem(storageKey(canvasId));
    if (!value) return emptyDocument();

    let parsed: unknown;
    try {
        parsed = JSON.parse(value);
    } catch {
        throw new Error("Agent 对话历史已损坏");
    }
    if (!isConversationDocument(parsed)) throw new Error("Agent 对话历史格式无效");
    return parsed;
}

export async function saveCloudAgentConversations(canvasId: string, activeId: string | null, conversations: CloudAgentConversation[]) {
    if (!canvasId) throw new Error("缺少画布 ID，无法保存 Agent 对话");
    const document: CloudAgentConversationDocument = { version: 1, activeId, conversations };
    await localForageStorageForScope(getActiveUserScope()).setItem(storageKey(canvasId), JSON.stringify(document));
}

// Keep the retry key outside the chat snapshot. If the response is lost, a
// page refresh can safely repeat the same request without creating another
// billed Agent turn.
export async function loadCloudAgentPendingSubmission(canvasId: string, conversationId: string) {
    if (!canvasId || !conversationId) return null;
    const value = await localForageStorageForScope(getActiveUserScope()).getItem(pendingStorageKey(canvasId, conversationId));
    if (value === null || value === undefined) return null;
    // A corrupt recovery record is not proof that the previous POST never ran.
    // Fail closed rather than discarding its identity and risking another charge.
    try {
        const parsed = JSON.parse(value) as Partial<CloudAgentPendingSubmission> | null;
        if (!parsed || typeof parsed.fingerprint !== "string" || typeof parsed.key !== "string" || parsed.key.length < 8) throw new Error("invalid identity");
        if (parsed.request !== undefined && (!parsed.request || parsed.request.idempotencyKey !== parsed.key || parsed.request.canvasId !== canvasId || typeof parsed.request.prompt !== "string")) throw new Error("invalid request");
        if (parsed.parentRunId !== undefined && typeof parsed.parentRunId !== "string") throw new Error("invalid parent");
        if (parsed.messageId !== undefined && typeof parsed.messageId !== "string") throw new Error("invalid message");
        return { fingerprint: parsed.fingerprint, key: parsed.key, request: parsed.request, parentRunId: parsed.parentRunId, messageId: parsed.messageId } satisfies CloudAgentPendingSubmission;
    } catch {
        throw new Error("待确认请求记录损坏，已暂停本对话发送；请先在任务中心核对原运行，不要直接重复生成");
    }
}

export async function saveCloudAgentPendingSubmission(canvasId: string, conversationId: string, pending: CloudAgentPendingSubmission) {
    if (!canvasId || !conversationId) throw new Error("缺少 Agent 对话范围，无法保存幂等提交");
    await localForageStorageForScope(getActiveUserScope()).setItem(pendingStorageKey(canvasId, conversationId), JSON.stringify(pending));
}

export async function clearCloudAgentPendingSubmission(canvasId: string, conversationId: string) {
    if (!canvasId || !conversationId) return;
    await localForageStorageForScope(getActiveUserScope()).removeItem(pendingStorageKey(canvasId, conversationId));
}

export function cloudAgentConversationTitle(messages: CloudAgentConversationMessage[]) {
    const firstPrompt = displayConversationTitle(messages.find((message) => message.role === "user")?.text || "");
    return firstPrompt.length > 28 ? `${firstPrompt.slice(0, 28)}...` : firstPrompt;
}

function displayConversationTitle(value: string) {
    return markdownPlainText(value.replace(/@\[skill:[^\]]+\]/gu, "技能包")) || "新对话";
}

function storageKey(canvasId: string) {
    return `${CLOUD_AGENT_CONVERSATIONS_KEY}:${encodeURIComponent(canvasId)}`;
}

function pendingStorageKey(canvasId: string, conversationId: string) {
    return `${CLOUD_AGENT_CONVERSATIONS_KEY}:pending:${encodeURIComponent(canvasId)}:${encodeURIComponent(conversationId)}`;
}

// ---------------------------------------------------------------------------
// 服务端镜像（跨设备恢复）。复用 /agent/conversations 端点（agentProfiles 表，
// scope=agent-conv）。任何失败都静默降级为纯本地模式：同步是增强，不是依赖。
// ---------------------------------------------------------------------------

export type CloudAgentConversationsRemote = {
    content: string | null;
    revision: number;
};

/** 拉取服务端快照。远端没有快照时 content 为 null、revision 为 0。 */
export async function pullCloudAgentConversationsRemote(canvasId: string): Promise<CloudAgentConversationsRemote | null> {
    if (!canvasId) return null;
    try {
        return await http.get<CloudAgentConversationsRemote>("/agent/conversations", { params: { canvasId }, timeout: 15_000 });
    } catch (error) {
        console.warn("拉取 Agent 会话快照失败，仅使用本地历史", error);
        return null;
    }
}

/**
 * 推送快照到服务端，返回服务端 revision。409（乐观锁冲突）返回 null，由调用方
 * 决定是否重新拉取合并重试；其余失败同样返回 null（保持本地模式）。
 */
export async function pushCloudAgentConversationsRemote(canvasId: string, document: CloudAgentConversationDocument, expectedRevision: number): Promise<number | null> {
    if (!canvasId) return null;
    try {
        const result = await http.put<CloudAgentConversationsRemote>("/agent/conversations", {
            canvasId,
            content: JSON.stringify(document),
            revision: expectedRevision,
        }, { timeout: 20_000 });
        return typeof result.revision === "number" ? result.revision : null;
    } catch (error) {
        // 冲突不算故障：返回 null 让调用方走「拉取→合并→重推」。
        if (isConflictError(error)) return null;
        console.warn("推送 Agent 会话快照失败，本地历史不受影响", error);
        return null;
    }
}

function isConflictError(error: unknown) {
    const message = error instanceof Error ? error.message : typeof error === "string" ? error : "";
    return message.includes("会话快照已变化") || message.includes("409");
}

/** 合并本地与服务端会话文档：按会话 id 去重，updatedAt 新者胜，单边存在的直接并入。 */
export function mergeCloudAgentConversations(local: CloudAgentConversationDocument, remote: CloudAgentConversationDocument): CloudAgentConversationDocument {
    const byId = new Map<string, CloudAgentConversation>();
    for (const conversation of remote.conversations) byId.set(conversation.id, conversation);
    for (const conversation of local.conversations) {
        const existing = byId.get(conversation.id);
        if (!existing || (conversation.updatedAt || "") >= (existing.updatedAt || "")) byId.set(conversation.id, conversation);
    }
    const conversations = [...byId.values()].sort((a, b) => (b.updatedAt || "").localeCompare(a.updatedAt || ""));
    const activeId = local.activeId && byId.has(local.activeId)
        ? local.activeId
        : remote.activeId && byId.has(remote.activeId)
            ? remote.activeId
            : conversations[0]?.id ?? null;
    return { version: 1, activeId, conversations };
}

/** 解析服务端快照内容；格式无效时返回 null（服务端数据不可信，回落本地）。 */
export function parseCloudAgentConversationsRemote(content: string | null | undefined): CloudAgentConversationDocument | null {
    if (!content) return null;
    try {
        const parsed: unknown = JSON.parse(content);
        return isConversationDocument(parsed) ? parsed : null;
    } catch {
        return null;
    }
}

function emptyDocument(): CloudAgentConversationDocument {
    return { version: 1, activeId: null, conversations: [] };
}

function isConversationDocument(value: unknown): value is CloudAgentConversationDocument {
    if (!value || typeof value !== "object") return false;
    const document = value as Partial<CloudAgentConversationDocument>;
    if (document.version !== 1 || !Array.isArray(document.conversations)) return false;
    return document.conversations.every((conversation) => {
        if (!conversation || typeof conversation !== "object") return false;
        const candidate = conversation as Partial<CloudAgentConversation>;
        return typeof candidate.id === "string"
            && typeof candidate.title === "string"
            && Array.isArray(candidate.messages)
            && typeof candidate.createdAt === "string"
            && typeof candidate.updatedAt === "string"
            && ["read_only", "auto", "request_approval"].includes(candidate.permissionMode || "");
    });
}
