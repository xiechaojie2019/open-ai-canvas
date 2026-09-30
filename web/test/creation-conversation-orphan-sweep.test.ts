import { expect, test } from "bun:test";

import { sweepOrphanedCreationMessages } from "../src/services/creation-conversation-store";

const NOW = Date.parse("2026-09-29T12:00:00.000Z");
const STALE = new Date(NOW - 60 * 60 * 1000).toISOString();
const FRESH = new Date(NOW - 60 * 1000).toISOString();

test("创作水合把无任务绑定的过期 pending 媒体消息收敛为失败态", () => {
    const conversations = [
        {
            id: "conversation-0001",
            messages: [
                { id: "assistant-stale", role: "assistant", mode: "video", status: "pending", createdAt: STALE },
                { id: "assistant-fresh", role: "assistant", mode: "video", status: "pending", createdAt: FRESH },
                { id: "assistant-bound", role: "assistant", mode: "video", status: "pending", createdAt: STALE, taskIds: ["task-0001"] },
                { id: "assistant-done", role: "assistant", mode: "image", status: "done", createdAt: STALE },
            ],
        },
    ];
    const swept = sweepOrphanedCreationMessages(conversations as never, NOW);
    const messages = swept[0].messages as Array<{ id: string; status?: string; content?: string }>;
    expect(messages.find((item) => item.id === "assistant-stale")?.status).toBe("error");
    expect(messages.find((item) => item.id === "assistant-stale")?.content).toBe("生成中断，请重新发送");
    expect(messages.find((item) => item.id === "assistant-fresh")?.status).toBe("pending");
    expect(messages.find((item) => item.id === "assistant-bound")?.status).toBe("pending");
    expect(messages.find((item) => item.id === "assistant-done")?.status).toBe("done");
});

test("没有孤儿消息时返回原数组引用，避免多余的水合写回", () => {
    const conversations = [
        {
            id: "conversation-0001",
            messages: [
                { id: "assistant-bound", role: "assistant", mode: "video", status: "pending", createdAt: STALE, taskIds: ["task-0001"] },
            ],
        },
    ];
    expect(sweepOrphanedCreationMessages(conversations as never, NOW)).toBe(conversations);
});
