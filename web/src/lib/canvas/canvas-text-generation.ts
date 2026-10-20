import { mediaHistoryEntries, selectMediaHistoryVersion } from "@/lib/canvas/canvas-media-history";
import type { CanvasNodeData, CanvasNodeMetadata, CanvasNodeTypeId } from "@/types/canvas";

/**
 * 配置节点类型（`CanvasNodeType.Config` 的字面量）。
 *
 * 这里刻意用字面量而不是 `CanvasNodeType.Config` 枚举：本模块被测试直接引入，
 * 用 `import type` 可以保证编译产物不残留 `@/types/canvas` 的运行时依赖。
 */
const CONFIG_NODE_TYPE = "config" as CanvasNodeTypeId;

/**
 * 文本生成是否可以原地写回来源节点。
 *
 * 只有配置节点（Config）例外：它本身不承载正文，生成结果必须落到新节点上。
 *
 * ⚠️ 历史上这里还排除了「来源文本已有正文」的改写场景（`editingTextNode`），
 * 结果改写会**新拉一个节点**（旧节点留着不动），与「在原节点上改」的直觉相反。
 * 现在改写也原地写回：旧正文压进 `metadata.mediaHistory`，可切回。
 */
export function canGenerateTextInPlace(sourceNode: CanvasNodeData | undefined): boolean {
    return sourceNode?.type !== CONFIG_NODE_TYPE;
}

/**
 * 原地生成失败/取消时回滚：把刚刚压进历史的旧正文取回顶层。
 *
 * 原地生成开始时旧正文已经被压进 `mediaHistory`、顶层 `content` 被清空。
 * 一旦这次生成失败，节点就会变成「没有正文、状态还停在 loading」的空壳 ——
 * 失败处理对「来源节点已有正文」的场景本来就不改状态，于是节点永远卡住。
 * 这里把旧正文取回顶层，并按传入的状态收尾（失败=error / 取消=idle）。
 */
export function rollbackTextInPlaceGeneration(metadata: CanvasNodeMetadata | undefined, historyId: string, status: "error" | "idle", richText?: Record<string, unknown>): CanvasNodeMetadata {
    const history = mediaHistoryEntries(metadata);
    // 历史里已经没有这一条（说明本来就没有旧正文，或已被别的写入方处理）→ 只改状态。
    if (!history.some((entry) => entry.id === historyId)) {
        return { ...metadata, status };
    }
    const restored = selectMediaHistoryVersion(metadata, historyId);
    // 旧正文的富文本层在 push 时被丢掉了（历史条目只存纯文本）。
    // 失败回滚要把排版一起还回去，否则用户会看到「文字回来了但排版没了」。
    const next: CanvasNodeMetadata = richText ? { ...restored, status, richText } : { ...restored, status };
    // 旧正文被取回顶层后历史可能已空：空数组留着只是噪音，删掉更干净。
    if (Array.isArray(next.mediaHistory) && next.mediaHistory.length === 0) delete next.mediaHistory;
    return next;
}
