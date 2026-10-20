import { canvasMediaSourceKey, isMediaAggregationTarget } from "@/lib/canvas/canvas-media-history";
import { hasImageBatchResult } from "@/lib/canvas/canvas-image-batch-retry";
import { isCanvasImageSourceNode } from "@/lib/canvas/canvas-image-source";
import { CanvasNodeType, type CanvasNodeData } from "@/types/canvas";

/**
 * 解析本次图片生成的结果落点节点。
 *
 * ⚠️ 归并 key（`canvasMediaSourceKey`）**只认参考图集合**：用了同一张参考图的兄弟节点
 * key 完全相同。早先直接 `nodes.find(...)` 取第一个命中项，命中的是数组里位置靠前的那个，
 * 于是「在下面那个节点上改提示词」结果却生成到了上面那个节点。
 *
 * 规则（顺序即优先级）：
 * 1. `reuseSourceNode`（空节点/变体副本原地生成）→ `undefined`，由调用方原地写回来源节点；
 * 2. 来源节点自己就是上一轮的生成结果图（未锁定、不是上传的输入素材）→ **它自己**。
 *    「在哪个节点上改，结果就写回哪个节点」；旧图由调用方压进它的 `mediaHistory`；
 * 3. 其余情况（文生图首次生成、来源节点是上传输入图/配置节点等）沿用既有归并契约：
 *    找同 key 的既有结果节点，找不到才新建。
 */
export function resolveImageAggregationTarget(
    sourceNode: CanvasNodeData | undefined,
    workingNodes: readonly CanvasNodeData[],
    sourceKey: string,
    reuseSourceNode: boolean,
): CanvasNodeData | undefined {
    if (reuseSourceNode) return undefined;
    const isImageNode = sourceNode?.type === CanvasNodeType.Image;
    // 上传输入图（isCanvasImageSourceNode）没有生成语义，不能被生成结果覆盖。
    const reuseOwnResultNode =
        isImageNode &&
        hasImageBatchResult(sourceNode as CanvasNodeData) &&
        !sourceNode?.metadata?.locked &&
        !isCanvasImageSourceNode(sourceNode);
    if (reuseOwnResultNode) return sourceNode;
    return workingNodes.find((node) => node.type === CanvasNodeType.Image && !node.metadata?.locked && isMediaAggregationTarget(node.metadata, sourceKey));
}

/** 本次生成是否会走「复用来源节点自己」这条路径（失败/取消需要据此回滚旧产物）。 */
export function isOwnResultAggregationTarget(target: CanvasNodeData | undefined, sourceNodeId: string): boolean {
    return Boolean(target && target.id === sourceNodeId);
}

export { canvasMediaSourceKey };