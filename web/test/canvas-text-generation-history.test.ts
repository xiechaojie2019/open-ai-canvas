import { describe, expect, test } from "bun:test";

import { canvasMediaSourceKey, isMediaAggregationTarget, MEDIA_HISTORY_LIMIT, pushCurrentMediaToHistory, selectMediaHistoryVersion } from "../src/lib/canvas/canvas-media-history";
import { canGenerateTextInPlace, rollbackTextInPlaceGeneration } from "../src/lib/canvas/canvas-text-generation";
import { CanvasNodeType, type CanvasNodeData } from "../src/types/canvas";

/**
 * 文本节点的历史契约。
 *
 * 文本**不参与**「同一来源归并到同一节点」（那是图片/视频的机制）：文本生成要么原地
 * 写回来源节点、要么每次新拉节点。因此历史只服务于**原地生成**——同一个节点反复生成
 * 时旧正文进 `metadata.mediaHistory`，可切回。
 * 文本的产物就是 `metadata.content`，与媒体共用 media-history 这一套数据结构。
 */
function textNode(id: string, metadata: Partial<NonNullable<CanvasNodeData["metadata"]>> = {}): CanvasNodeData {
    return {
        id,
        type: CanvasNodeType.Text,
        title: id,
        position: { x: 0, y: 0 },
        width: 320,
        height: 200,
        metadata,
    };
}

describe("canvas text generation history", () => {
    test("文本不写来源标记：不参与图片/视频那一套节点归并", () => {
        // 归并 key 是媒体节点的机制；文本若也带上，同key 的手动生成会被误当成归并目标。
        const key = canvasMediaSourceKey("text", "config-1", []);
        expect(key).toBe("v1|text|config-1");
        // 文本与图片的 key 本就不该命中对方。
        expect(canvasMediaSourceKey("text", "n1", [])).not.toBe(canvasMediaSourceKey("image", "n1", []));
        // 带来源标记的节点才会被当成归并目标复用，文本节点永远不带这个标记。
        expect(isMediaAggregationTarget({ generationSourceKey: key }, key)).toBe(true);
        expect(isMediaAggregationTarget({ content: "手写或生成的正文" }, key)).toBe(false);
        expect(isMediaAggregationTarget(undefined, key)).toBe(false);
    });

    test("旧正文入历史并从顶层移除，新正文写回顶层 content", () => {
        const source = textNode("t1", { content: "第一版正文", prompt: "提示词A", generationSourceKey: canvasMediaSourceKey("text", "config-1", []) });
        const pushed = pushCurrentMediaToHistory(source.metadata, { id: "hist-1" });

        expect(pushed.content).toBeUndefined();
        expect(pushed.mediaHistory).toHaveLength(1);
        expect(pushed.mediaHistory?.[0]).toMatchObject({ id: "hist-1", content: "第一版正文", prompt: "提示词A" });
    });

    test("富文本层必须与正文一起丢弃，否则渲染出旧排版+新正文", () => {
        const source = textNode("t1", { content: "正文", richText: { type: "doc", content: [] } });
        expect(pushCurrentMediaToHistory(source.metadata, { id: "hist-1" }).richText).toBeUndefined();
    });

    test("空文本节点入栈是幂等空操作（待填节点不该产生空历史条目）", () => {
        const empty = textNode("t1", { status: "idle" });
        const pushed = pushCurrentMediaToHistory(empty.metadata, { id: "hist-1" });
        expect(pushed.mediaHistory).toBeUndefined();
    });

    test("文本没有 storageKey，按正文去重（回写重试不重复入栈）", () => {
        const once = pushCurrentMediaToHistory({ content: "同一段正文" }, { id: "hist-1" });
        const twice = pushCurrentMediaToHistory(once, { id: "hist-2" });
        expect(twice.mediaHistory).toHaveLength(1);
        expect(twice.mediaHistory?.[0].id).toBe("hist-1");
    });

    test("切换历史版本：选中项提到顶层，来回切换不丢结果", () => {
        const first = pushCurrentMediaToHistory({ content: "第一版" }, { id: "hist-1" });
        const second = pushCurrentMediaToHistory({ ...first, content: "第二版" }, { id: "hist-2" });
        expect(second.content).toBeUndefined();
        expect(second.mediaHistory?.map((entry) => entry.content)).toEqual(["第二版", "第一版"]);

        // 顶层此刻是空的（第二版还没回写），切到第一版：它被提到顶层，从历史移除。
        // 历史里不能多出空占位把刚选中的那条挤掉。
        const backToFirst = selectMediaHistoryVersion(second, "hist-1");
        expect(backToFirst.content).toBe("第一版");
        expect(backToFirst.status).toBe("success");
        expect(backToFirst.mediaHistory?.map((entry) => entry.content)).toEqual(["第二版"]);

        // 再切回第二版：第一版作为「当前产物」回到历史，两版都在，一条不少。
        const forwardAgain = selectMediaHistoryVersion(backToFirst, "hist-2");
        expect(forwardAgain.content).toBe("第二版");
        expect(forwardAgain.mediaHistory?.map((entry) => entry.content)).toEqual(["第一版"]);
    });

    test("切换不存在的条目时原样返回，不破坏节点", () => {
        const source = textNode("t1", { content: "正文", mediaHistory: [{ id: "hist-1", content: "旧版" }] });
        const next = selectMediaHistoryVersion(source.metadata, "hist-missing");
        expect(next.content).toBe("正文");
        expect(next.mediaHistory).toHaveLength(1);
    });

    test("已有正文的文本节点（改写场景）也必须原地写回，不能新拉节点", () => {
        // 回归：曾经用 `!isConfigNode && !editingTextNode` 判定，把「改写已有文本」排除在外，
        // 结果改写会新拉一个节点（原节点留着不动），与「在原节点上改」的直觉相反。
        const editing = textNode("t1", { content: "旧正文" });
        expect(canGenerateTextInPlace(editing)).toBe(true);

        // 配置节点本身不承载正文，生成结果必须落到新节点上。
        expect(canGenerateTextInPlace({ ...editing, type: CanvasNodeType.Config })).toBe(false);
        // 空文本节点（待填）也原地写回。
        expect(canGenerateTextInPlace(textNode("t1"))).toBe(true);
        // 来源节点不存在时按「非配置节点」处理（与改动前的判定一致）。
        expect(canGenerateTextInPlace(undefined)).toBe(true);
    });

    test("改写原地写回：旧正文入历史，新正文回写顶层，可切回旧版", () => {
        const source = textNode("t1", { content: "旧正文" });
        // 原地生成开始：旧正文入历史、顶层 content 被清空
        const pending = pushCurrentMediaToHistory(source.metadata, { id: "hist-1" });
        expect(pending.content).toBeUndefined();
        expect(pending.mediaHistory?.[0].content).toBe("旧正文");

        // 任务回写新正文
        const done = { ...pending, content: "新正文" };
        expect(done.content).toBe("新正文");
        // 切回旧版
        const back = selectMediaHistoryVersion(done, "hist-1");
        expect(back.content).toBe("旧正文");
    });

    test("原地生成失败：旧正文必须回到顶层，节点不能变成没有正文的空壳", () => {
        const source = textNode("t1", { content: "旧正文", richText: { type: "doc" } });
        const pending = pushCurrentMediaToHistory(source.metadata, { id: "hist-1" });
        expect(pending.content).toBeUndefined();

        // 失败回滚：旧正文取回顶层，状态落到 error。
        const rolled = rollbackTextInPlaceGeneration(pending, "hist-1", "error", source.metadata?.richText);
        expect(rolled.content).toBe("旧正文");
        expect(rolled.status).toBe("error");
        // 排版也一起还回去。
        expect(rolled.richText).toEqual({ type: "doc" });
        // 旧正文被取回顶层后不应同时留在历史里（避免同一条出现两次）。
        expect(rolled.mediaHistory).toBeUndefined();
    });

    test("原地生成取消：状态回到 idle，旧正文同样回到顶层", () => {
        const source = textNode("t1", { content: "旧正文" });
        const pending = pushCurrentMediaToHistory(source.metadata, { id: "hist-1" });
        const rolled = rollbackTextInPlaceGeneration(pending, "hist-1", "idle");
        expect(rolled.content).toBe("旧正文");
        expect(rolled.status).toBe("idle");
    });

    test("原地生成失败但本来没有旧正文：只改状态，不凭空造历史", () => {
        const pending = { ...textNode("t1").metadata, status: "loading" as const };
        const rolled = rollbackTextInPlaceGeneration(pending, "hist-1", "error");
        expect(rolled.content).toBeUndefined();
        expect(rolled.status).toBe("error");
        expect(rolled.mediaHistory).toBeUndefined();
    });

    test("历史上限 30 条，超出丢弃最旧", () => {
        let metadata: NonNullable<CanvasNodeData["metadata"]> = {};
        for (let i = 0; i < MEDIA_HISTORY_LIMIT + 5; i += 1) {
            metadata = pushCurrentMediaToHistory(metadata, { id: `hist-${i}` });
            metadata = { ...metadata, content: `第${i}版` };
        }
        expect(metadata.mediaHistory).toHaveLength(MEDIA_HISTORY_LIMIT);
        // 最新在前，最旧的已被丢弃
        expect(metadata.mediaHistory?.[0].content).toBe(`第${MEDIA_HISTORY_LIMIT + 3}版`);
    });
});