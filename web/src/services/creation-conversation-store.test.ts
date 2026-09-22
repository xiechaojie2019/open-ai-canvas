import assert from "node:assert/strict";
import test from "node:test";

// @ts-expect-error -- Node 原生 TypeScript 测试运行器需要保留扩展名。
import { pendingCreationTaskIds } from "./creation-conversation-store.ts";

test("媒体消息素材化失败后仍会回查后端任务", () => {
    const ids = pendingCreationTaskIds([
        {
            id: "conversation-1",
            messages: [{ id: "message-1", role: "assistant", mode: "video", status: "error", taskIds: ["task-1"] }],
        },
    ]);

    assert.deepEqual(ids, ["task-1"]);
});

test("没有任务身份的媒体错误不会进入恢复轮询", () => {
    const ids = pendingCreationTaskIds([
        {
            id: "conversation-1",
            messages: [{ id: "message-1", role: "assistant", mode: "video", status: "error" }],
        },
    ]);

    assert.deepEqual(ids, []);
});
