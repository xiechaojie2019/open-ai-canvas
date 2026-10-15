import { describe, expect, test } from "bun:test";

import { ordinaryCanvasProjectSnapshot, registerCanvasGenerationPersistenceAttempt, type CanvasProject } from "../src/stores/canvas/use-canvas-store";
import { CanvasNodeType, type CanvasAssistantSession, type CanvasConnection, type CanvasNodeData } from "../src/types/canvas";

/**
 * 回归用例：ordinaryCanvasProjectSnapshot 曾把「durable 快照落后」当成「生成未确认」，
 * 一次性删掉 4 个图片节点 + 11 条连线（线上画布 tKoXfYCky7phJ7mc2cs2A，revision 1490）。
 * 结构完全复刻该画布：12 节点 / 12 连线，其中 4 个图片节点带 attach-node 戳。
 */

const SCOPE = "regression-scope";
const PROJECT_ID = "canvas-regression";

const ALL_NODE_IDS = [
    "kx7q2", "catdog2", "sbhut", "catpig1", "ipgdb", "catbg4-1",
    "catbg4-2", "catbg4-3", "catbg4-4", "puppy1", "teemogreen1", "orangecat1",
] as const;

/** 线上只有这 4 个节点带 attach-node 戳，其余 8 个都没有——这是事故里 100% 的切分线。 */
const STAMPED: Record<string, string[]> = {
    sbhut: ["attach-node:a:image-1791960675183-sbhut:0", "attach-node:b:image-1791960675183-sbhut:0"],
    ipgdb: ["attach-node:c:image-1791962862402-ipgdb:0", "attach-node:d:image-1791962862402-ipgdb:0", "attach-node:e:image-1791962862402-ipgdb:0"],
    "catbg4-3": ["attach-node:f:image-1792200000000-catbg4-3:0"],
    "catbg4-4": ["attach-node:g:image-1792200000000-catbg4-4:0"],
};

/** 12 条连线，最后一条 target 早已不存在，是历史遗留的悬空连线。 */
const LIVE_CONNECTIONS: Array<[string, string, string]> = [
    ["c-01", "kx7q2", "catdog2"],
    ["c-02", "kx7q2", "sbhut"],
    ["c-03", "kx7q2", "catpig1"],
    ["c-04", "kx7q2", "ipgdb"],
    ["c-05", "kx7q2", "catbg4-1"],
    ["c-06", "kx7q2", "catbg4-2"],
    ["c-07", "kx7q2", "catbg4-3"],
    ["c-08", "kx7q2", "catbg4-4"],
    ["c-09", "kx7q2", "puppy1"],
    ["c-10", "kx7q2", "teemogreen1"],
    ["c-11", "kx7q2", "orangecat1"],
    ["c-12", "kx7q2", "on819"],
];

function node(id: string, generationEffectKeys?: string[]): CanvasNodeData {
    return {
        id,
        type: CanvasNodeType.Image,
        title: id,
        position: { x: 0, y: 0 },
        width: 100,
        height: 100,
        metadata: generationEffectKeys ? { generationEffectKeys } : undefined,
    };
}

function connection([id, fromNodeId, toNodeId]: [string, string, string]): CanvasConnection {
    return { id, fromNodeId, toNodeId };
}

function session(id: string, generationEffectKeys?: string[]): CanvasAssistantSession {
    return {
        id,
        title: id,
        messages: [],
        generationEffectKeys,
        createdAt: "2026-10-14T00:00:00Z",
        updatedAt: "2026-10-14T00:00:00Z",
    };
}

function project(nodes: CanvasNodeData[], connections: CanvasConnection[], chatSessions: CanvasAssistantSession[] = [], activeChatId: string | null = null): CanvasProject {
    return {
        id: PROJECT_ID,
        revision: 1490,
        title: PROJECT_ID,
        createdAt: "2026-10-14T00:00:00Z",
        updatedAt: "2026-10-14T00:00:00Z",
        nodes,
        connections,
        chatSessions,
        activeChatId,
        backgroundMode: "dots",
        showImageInfo: false,
        viewport: { x: 0, y: 0, k: 1 },
        directorScenes: [],
    };
}

/** 复刻事故里的 durable 快照：只剩 8 个无戳节点和 1 条最老的连线。 */
function staleDurableProject(): CanvasProject {
    const nodes = ALL_NODE_IDS.filter((id) => !STAMPED[id]).map((id) => node(id));
    return project(nodes, [connection(["c-01", "kx7q2", "catdog2"])]);
}

function liveProject(): CanvasProject {
    return project(
        ALL_NODE_IDS.map((id) => node(id, STAMPED[id])),
        LIVE_CONNECTIONS.map(connection),
    );
}

describe("ordinaryCanvasProjectSnapshot 不得凭过期 durable 快照删除用户内容", () => {
    test("durable 缺失但键已确认的图片节点必须保留，连线不得被整份回退", () => {
        const projected = ordinaryCanvasProjectSnapshot(SCOPE, liveProject(), staleDurableProject());

        // 4 个带戳图片节点一个都不能丢——这是事故里被删掉的内容。
        expect(projected.nodes.map((item) => item.id)).toEqual([...ALL_NODE_IDS]);
        // 连线保留实时内容，而不是被 durable 的那 1 条替换掉。
        // 唯一被清掉的是 target 早已不存在的 c-12（悬空连线，本就应该清）。
        expect(projected.connections.map((item) => item.id)).toEqual([
            "c-01", "c-02", "c-03", "c-04", "c-05", "c-06", "c-07", "c-08", "c-09", "c-10", "c-11",
        ]);
    });

    test("端点已消失的连线仍会被清理，避免悬空连线", () => {
        const durable = project([node("kx7q2")], []);
        const live = project(
            [node("kx7q2"), node("sbhut", STAMPED.sbhut)],
            [connection(["c-1", "kx7q2", "sbhut"]), connection(["c-dangling", "kx7q2", "gone-node"])],
        );

        const projected = ordinaryCanvasProjectSnapshot(SCOPE, live, durable);

        expect(projected.nodes.map((item) => item.id)).toEqual(["kx7q2", "sbhut"]);
        expect(projected.connections.map((item) => item.id)).toEqual(["c-1"]);
    });

    test("真正由在途生成新建的乐观节点仍按原语义扣下", () => {
        const effectKey = "attach-node:zz:image-optimistic:0";
        const durable = project([node("kx7q2")], [connection(["c-1", "kx7q2", "catdog2"])]);
        const live = project(
            [node("kx7q2"), node("optimistic", [effectKey])],
            [connection(["c-1", "kx7q2", "catdog2"])],
        );

        const unregister = registerCanvasGenerationPersistenceAttempt(SCOPE, PROJECT_ID, effectKey, {
            previousNodes: [node("kx7q2")],
            nodes: [node("kx7q2"), node("optimistic", [effectKey])],
        });
        try {
            const projected = ordinaryCanvasProjectSnapshot(SCOPE, live, durable);
            expect(projected.nodes.map((item) => item.id)).toEqual(["kx7q2"]);
        } finally {
            unregister();
        }
    });

    test("durable 中已有节点时仍按 durable 版本回滚未确认的戳", () => {
        const durable = project(
            [node("kx7q2"), node("sbhut", ["attach-node:confirmed"])],
            [connection(["c-1", "kx7q2", "sbhut"])],
        );
        const live = project(
            [node("kx7q2"), node("sbhut", ["attach-node:confirmed", "attach-node:unconfirmed"])],
            [connection(["c-1", "kx7q2", "sbhut"])],
        );

        const projected = ordinaryCanvasProjectSnapshot(SCOPE, live, durable);

        expect(projected.nodes.map((item) => item.metadata?.generationEffectKeys)).toEqual([undefined, ["attach-node:confirmed"]]);
    });

    test("durable 缺失的会话默认保留，不凭过期快照删对话", () => {
        const durable = project([node("kx7q2")], [], [session("chat-old")], "chat-old");
        const live = project(
            [node("kx7q2")],
            [],
            [session("chat-old"), session("chat-new", ["attach-node:a:chat-new:0"])],
            "chat-new",
        );

        const projected = ordinaryCanvasProjectSnapshot(SCOPE, live, durable);

        expect(projected.chatSessions.map((item) => item.id)).toEqual(["chat-old", "chat-new"]);
        expect(projected.activeChatId).toBe("chat-new");
    });

    test("没有任何生成戳时原样返回同一个对象", () => {
        const live = project([node("kx7q2")], [connection(["c-1", "kx7q2", "catdog2"])]);
        const durable = project([node("kx7q2")], []);

        expect(ordinaryCanvasProjectSnapshot(SCOPE, live, durable)).toBe(live);
    });
});
