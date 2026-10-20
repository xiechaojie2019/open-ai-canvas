import { describe, expect, test } from "bun:test";
import { resolveImageAggregationTarget, isOwnResultAggregationTarget } from "../src/lib/canvas/canvas-image-aggregation-target";
import { canvasMediaSourceKey, rollbackMediaInPlaceGeneration, pushCurrentMediaToHistory } from "../src/lib/canvas/canvas-media-history";
import { CanvasNodeType, type CanvasNodeData } from "../src/types/canvas";

function resultNode(id: string, y: number, sourceKey: string): CanvasNodeData {
    return {
        id,
        type: CanvasNodeType.Image,
        title: id,
        position: { x: 400, y },
        width: 300,
        height: 300,
        metadata: {
            content: `data:image/png;base64,${id}`,
            storageKey: `user:${id}:file`,
            generationType: "edit",
            generationSourceKey: sourceKey,
        },
    };
}

describe("image generation aggregation target", () => {
    // 线上复现：两个节点用同一张参考图（key 完全相同），
    // 在下面那个（B）上改提示词重新生成，图却出到了上面那个（A）。
    const sourceKey = canvasMediaSourceKey("image", "text-1", ["ref-1"]);
    const nodeA = resultNode("img-a", 0, sourceKey);
    const nodeB = resultNode("img-b", 400, sourceKey);

    test("同参考图的兄弟节点：在下面那个节点上生成必须命中自己", () => {
        const target = resolveImageAggregationTarget(nodeB, [nodeA, nodeB], sourceKey, false);
        expect(target?.id).toBe("img-b");
        expect(isOwnResultAggregationTarget(target, "img-b")).toBe(true);
    });

    test("数组顺序颠倒时依然命中来源节点自己，不受位置影响", () => {
        const target = resolveImageAggregationTarget(nodeB, [nodeB, nodeA], sourceKey, false);
        expect(target?.id).toBe("img-b");
    });

    test("来源节点不是生成结果图（上传输入图）时仍按 key 归并到既有结果节点", () => {
        const uploaded: CanvasNodeData = {
            id: "uploaded",
            type: CanvasNodeType.Image,
            title: "上传图",
            position: { x: 0, y: 0 },
            width: 300,
            height: 300,
            metadata: { content: "data:image/png;base64,up", storageKey: "user:up:file" },
        };
        const target = resolveImageAggregationTarget(uploaded, [nodeA, nodeB], canvasMediaSourceKey("image", "uploaded", ["ref-1"]), false);
        expect(target?.id).toBe("img-a");
        expect(isOwnResultAggregationTarget(target, "uploaded")).toBe(false);
    });

    test("锁定的结果节点不被复用为归并目标", () => {
        const locked = { ...nodeB, metadata: { ...nodeB.metadata, locked: true } } as CanvasNodeData;
        const target = resolveImageAggregationTarget(locked, [nodeA, locked], sourceKey, false);
        expect(target?.id).toBe("img-a");
    });

    test("空节点原地生成（reuseSourceNode）时不做归并查找", () => {
        const empty: CanvasNodeData = {
            id: "empty",
            type: CanvasNodeType.Image,
            title: "",
            position: { x: 0, y: 0 },
            width: 300,
            height: 300,
            metadata: {},
        };
        expect(resolveImageAggregationTarget(empty, [nodeA, nodeB], sourceKey, true)).toBeUndefined();
    });

    test("旧图入历史后失败回滚能把旧图取回顶层", () => {
        const pushed = pushCurrentMediaToHistory(nodeB.metadata, { id: "hist-fixed" });
        expect(pushed.content).toBeUndefined();
        expect(pushed.mediaHistory?.[0]?.storageKey).toBe("user:img-b:file");

        const rolled = rollbackMediaInPlaceGeneration({ ...pushed, status: "loading" }, "hist-fixed", "error");
        expect(rolled.storageKey).toBe("user:img-b:file");
        expect(rolled.status).toBe("error");
    });

    test("历史里没有对应条目时只改状态，不伪造产物", () => {
        const rolled = rollbackMediaInPlaceGeneration({ status: "loading" }, "hist-missing", "idle");
        expect(rolled.status).toBe("idle");
        expect(rolled.storageKey).toBeUndefined();
    });
});