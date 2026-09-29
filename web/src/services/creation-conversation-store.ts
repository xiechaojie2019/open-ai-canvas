import { localForageStorageForScope } from "@/lib/localforage-storage";
import { getActiveUserScope } from "@/lib/user-scope";

export const CREATION_CONVERSATIONS_KEY = "creation-conversations-v1";

type PendingCreationMessage = {
    id: string;
    role: "user" | "assistant";
    mode?: string;
    status?: string;
    taskIds?: string[];
    createdAt?: string;
    content?: unknown;
};

// 创建请求的响应在刷新或断网时丢失，会让 pending 消息永远等不到 taskIds，
// 恢复链路（isRecoverableCreationMessage）因此不再接管它，卡片将永久显示"正在生成"。
// 水合时把超过时长阈值仍无任务绑定的 pending 消息收敛为显式失败态，让用户可以重试。
const ORPHANED_PENDING_MESSAGE_MAX_AGE_MS = 30 * 60 * 1000;

export function sweepOrphanedCreationMessages<T extends StoredCreationConversation>(conversations: T[], now = Date.now()): T[] {
    let changed = false;
    const next = conversations.map((conversation) => {
        let conversationChanged = false;
        const messages = (conversation.messages || []).map((message) => {
            if (message.role !== "assistant" || message.taskIds?.length) return message;
            if (message.status !== "pending") return message;
            const createdAt = Date.parse(message.createdAt || "");
            if (!Number.isFinite(createdAt) || now - createdAt < ORPHANED_PENDING_MESSAGE_MAX_AGE_MS) return message;
            conversationChanged = true;
            return { ...message, status: "error", content: "生成中断，请重新发送" };
        });
        if (!conversationChanged) return conversation;
        changed = true;
        return { ...conversation, messages };
    });
    return changed ? next : conversations;
}

export type StoredCreationConversation = {
    id: string;
    messages: PendingCreationMessage[];
};

export function updateCreationConversationSnapshot<T extends { id: string }>(conversations: T[], conversationId: string, updater: (conversation: T) => T) {
    return conversations.map((conversation) => (conversation.id === conversationId ? updater(conversation) : conversation));
}

// 对话、生成任务与素材是独立持久状态；删除历史记录不能在这里级联清理任务或资源。
export function removeCreationConversationSnapshot<T extends { id: string }>(conversations: T[], conversationId: string) {
    if (!conversationId) throw new Error("缺少要删除的创作对话 ID");
    const next = conversations.filter((conversation) => conversation.id !== conversationId);
    if (next.length === conversations.length) throw new Error("要删除的创作对话不存在");
    return next;
}

function isRecoverableCreationMessage(message: PendingCreationMessage) {
    if (message.role !== "assistant" || !message.taskIds?.length) return false;
    // 媒体任务可能已成功，但浏览器在素材化或挂载消息时失败。错误消息也要回查后端任务，
    // 让已经持久化的成功结果覆盖瞬时的前端错误；真实失败仍由任务终态收敛为 error。
    return message.mode === "text" ? message.status === "streaming" || message.status === "pending" : message.status === "pending" || message.status === "error";
}

export function pendingCreationTaskKey(conversations: StoredCreationConversation[]) {
    return conversations
        .flatMap((conversation) => conversation.messages.flatMap((message) => (isRecoverableCreationMessage(message) ? [`${conversation.id}:${message.id}:${(message.taskIds || []).join(",")}`] : [])))
        .join("|");
}

export function pendingCreationTaskIds(conversations: StoredCreationConversation[]) {
    const taskIds = conversations.flatMap((conversation) =>
        conversation.messages.flatMap((message) => {
            if (!isRecoverableCreationMessage(message)) return [];
            return message.taskIds || [];
        }),
    );
    return Array.from(new Set(taskIds));
}

export async function loadCreationConversations<T extends StoredCreationConversation>() {
    const storage = localForageStorageForScope(getActiveUserScope());
    const value = await storage.getItem(CREATION_CONVERSATIONS_KEY);
    if (!value) return null;
    let parsed: unknown;
    try {
        parsed = JSON.parse(value);
    } catch {
        throw new Error("创作对话持久状态无效");
    }
    if (!Array.isArray(parsed)) throw new Error("创作对话持久状态无效");
    return parsed as T[];
}

export async function saveCreationConversations<T extends StoredCreationConversation>(conversations: T[]) {
    const storage = localForageStorageForScope(getActiveUserScope());
    await storage.setItem(CREATION_CONVERSATIONS_KEY, JSON.stringify(conversations));
}
