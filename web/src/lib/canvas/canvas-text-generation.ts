import { MEDIA_HISTORY_LIMIT, mediaHistoryEntries, selectMediaHistoryVersion } from "@/lib/canvas/canvas-media-history";
import type { CanvasMediaHistoryEntry, CanvasNodeData, CanvasNodeMetadata, CanvasNodeTypeId } from "@/types/canvas";

/**
 * 配置节点类型（`CanvasNodeType.Config` 的字面量）。
 *
 * 这里刻意用字面量而不是 `CanvasNodeType.Config` 枚举：本模块被测试直接引入，
 * 用 `import type` 可以保证编译产物不残留 `@/types/canvas` 的运行时依赖。
 */
const CONFIG_NODE_TYPE = "config" as CanvasNodeTypeId;

/** 文本结果节点类型（同样用字面量，避免编译产物带 @/types/canvas 的运行时依赖）。 */
const TEXT_NODE_TYPE = "text" as CanvasNodeTypeId;

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

/** 节点是否已经有可展示的文本结果（空串不算，生成中还没回写）。 */
export function hasTextBatchResult(node: CanvasNodeData | undefined): boolean {
    return Boolean(node?.metadata?.content?.trim());
}

/**
 * 清掉一个文本批次留在画布上的「聚合标记」。
 *
 * 生成期间子节点靠 `batchRootId` 指向承载节点，渲染层据此把它们藏起来，
 * 画布上只看到承载节点在 loading。批次结束后必须撤掉这些标记：
 * - 承载节点不再声称自己是批次根，避免下一轮生成复用旧子节点列表；
 * - 保留下来的失败子节点恢复可见，才能单独重试。
 *
 * 只处理文本节点；图片批次有自己的 reclaim 逻辑，不能被这里误清。
 */
export function clearTextBatchMarkers(nodes: CanvasNodeData[], rootId: string): CanvasNodeData[] {
    const root = nodes.find((node) => node.id === rootId);
    if (!root || root.type !== TEXT_NODE_TYPE) return nodes;
    return nodes.map((node) => {
        if (node.id === rootId) {
            if (!node.metadata?.isBatchRoot && !node.metadata?.batchChildIds?.length) return node;
            const metadata = { ...node.metadata };
            delete metadata.isBatchRoot;
            delete metadata.batchChildIds;
            delete metadata.imageBatchExpanded;
            return { ...node, metadata };
        }
        if (node.metadata?.batchRootId !== rootId) return node;
        const metadata = { ...node.metadata };
        delete metadata.batchRootId;
        return { ...node, metadata };
    });
}

/**
 * 文本批次收口：把一次生成的多份结果折成**一个**节点，其余进 `metadata.mediaHistory`。
 *
 * 为什么要先各落一个临时节点再收口：`commitCanvasGenerationResult` 不允许把结果写到
 * 「已绑定其他任务」的节点上，N 份结果并发写同一个节点会互相覆盖。所以子任务照旧
 * 各自落在自己的节点上，全部结束后再一次性合并。
 *
 * 规则：
 * - 原地生成（普通文本节点）：来源节点承载最新一份，其余成功子结果进它的历史；
 * - 配置节点不承载正文：第一个成功子节点成为承载节点，其余成功子结果进它的历史；
 * - 来源节点自身失败但子结果成功：把第一份成功结果提升到来源节点，节点不能停在空壳；
 * - 失败子节点保留（与图片批次一致），用来重试；
 * - 还有子任务在途时不动，返回原数组。
 */
export function finalizeCompletedTextBatch(
    sourceNodeId: string,
    childIds: readonly string[],
    nodes: CanvasNodeData[],
): { nodes: CanvasNodeData[]; removedIds: string[] } {
    const source = nodes.find((node) => node.id === sourceNodeId);
    if (!source || !childIds.length) return { nodes, removedIds: [] };
    const nodeById = new Map(nodes.map((node) => [node.id, node]));
    const children = childIds
        .map((id) => nodeById.get(id))
        .filter((node): node is CanvasNodeData => Boolean(node && node.type === TEXT_NODE_TYPE));
    if (!children.length) return { nodes: clearTextBatchMarkers(nodes, sourceNodeId), removedIds: [] };
    // 还有子任务没落地：等它出完再收口，否则把在途结果也当成失败节点留下。
    if (children.some((node) => !hasTextBatchResult(node) && node.metadata?.status === "loading")) {
        return { nodes, removedIds: [] };
    }
    const successful = children.filter(hasTextBatchResult);
    // 全失败：收口无事可做，但批次标记必须撤掉，否则失败的子节点会一直隐藏。
    if (!successful.length) return { nodes: clearTextBatchMarkers(nodes, sourceNodeId), removedIds: [] };

    const isConfigSource = source.type === CONFIG_NODE_TYPE;
    // 原地生成失败时来源节点是空的：把第一份成功结果提升上去，而不是让用户看到一张空节点。
    const promote = !isConfigSource && !hasTextBatchResult(source) ? successful[0] : undefined;
    const carrier = isConfigSource ? successful[0] : source;
    const folded = successful.filter((node) => node.id !== carrier.id && node.id !== promote?.id);
    const removedIds = [...folded.map((node) => node.id), ...(promote ? [promote.id] : [])];
    const removed = new Set(removedIds);

    const metadata: CanvasNodeMetadata = { ...carrier.metadata };
    const currentContent = promote?.metadata?.content ?? carrier.metadata?.content;
    const existing = mediaHistoryEntries(metadata);
    const knownIds = new Set(existing.map((entry) => entry.id));
    const knownContent = new Set(existing.map((entry) => entry.content).filter((content): content is string => Boolean(content)));
    if (currentContent) knownContent.add(currentContent);
    const fresh: CanvasMediaHistoryEntry[] = [];
    for (const child of folded) {
        const entry = textBatchHistoryEntry(child);
        if (!entry.content || knownIds.has(entry.id) || knownContent.has(entry.content)) continue;
        knownIds.add(entry.id);
        knownContent.add(entry.content);
        fresh.push(entry);
    }
    // 同一批里后出的排前面，与 mediaHistory「最新在前」的约定一致。
    if (fresh.length) metadata.mediaHistory = [...fresh.reverse(), ...existing].slice(0, MEDIA_HISTORY_LIMIT);
    if (promote) Object.assign(metadata, textResultFields(promote), { status: "success" as const, errorDetails: undefined, generationErrorCode: undefined, failedPromptFingerprint: undefined, resourceReloadAvailable: undefined });

    const next = nodes
        .filter((node) => !removed.has(node.id))
        .map((node) => (node.id === carrier.id ? { ...node, metadata } : node));
    return { nodes: clearTextBatchMarkers(next, sourceNodeId), removedIds };
}

/**
 * 刷新/恢复后补齐文本批次：在途的保持隐藏，已结束的走一次收口并撤掉标记。
 *
 * 正常流程由 executor 收口；但刷新会中断 executor，只剩画布上的节点状态。
 * 这里兜住「刷新后子节点永久隐藏」的情况。
 */
export function reconcileTextBatchRoots(nodes: CanvasNodeData[]): CanvasNodeData[] {
    let next = nodes;
    for (const candidate of nodes) {
        const root = next.find((node) => node.id === candidate.id);
        if (!root || root.type !== TEXT_NODE_TYPE || !root.metadata?.isBatchRoot) continue;
        const childIds = root.metadata.batchChildIds || [];
        const nodeById = new Map(next.map((node) => [node.id, node]));
        if (childIds.some((id) => nodeById.get(id)?.metadata?.status === "loading")) continue;
        const finalized = finalizeCompletedTextBatch(root.id, childIds, next);
        next = clearTextBatchMarkers(finalized.nodes, root.id);
    }
    return next;
}

/** 取消时清掉没出结果的占位节点，已完成的成功结果照常收口。 */
export function cancelIncompleteTextBatch(sourceNodeId: string, childIds: readonly string[], nodes: CanvasNodeData[]) {
    const nodeById = new Map(nodes.map((node) => [node.id, node]));
    const incomplete = new Set(
        childIds.filter((id) => {
            const node = nodeById.get(id);
            return Boolean(node && node.type === TEXT_NODE_TYPE && !hasTextBatchResult(node));
        }),
    );
    const withoutIncomplete = incomplete.size ? nodes.filter((node) => !incomplete.has(node.id)) : nodes;
    const finalized = finalizeCompletedTextBatch(sourceNodeId, childIds, withoutIncomplete);
    return { nodes: finalized.nodes, removedIds: [...incomplete, ...finalized.removedIds] };
}

function textResultFields(node: CanvasNodeData) {
    return {
        content: node.metadata?.content,
        richText: node.metadata?.richText,
        prompt: node.metadata?.prompt,
        composerContent: node.metadata?.composerContent,
        // 任务绑定一起搬过去：原节点自己失败、由子结果顶上时，不能让节点继续挂着失败任务。
        taskId: node.metadata?.taskId,
        taskClientOperationId: node.metadata?.taskClientOperationId,
        taskStatus: node.metadata?.taskStatus,
        taskStartedAt: node.metadata?.taskStartedAt,
        taskCompletedAt: node.metadata?.taskCompletedAt,
        taskDurationMs: node.metadata?.taskDurationMs,
        taskCreatedAt: node.metadata?.taskCreatedAt,
        taskUpdatedAt: node.metadata?.taskUpdatedAt,
        attemptGroupId: node.metadata?.attemptGroupId,
        retryOf: node.metadata?.retryOf,
        taskErrorCode: undefined,
    };
}

function textBatchHistoryEntry(node: CanvasNodeData): CanvasMediaHistoryEntry {
    return {
        // 用子节点 id 派生：收口重试不会产生重复条目。
        id: `hist-${node.id}`,
        content: node.metadata?.content,
        prompt: node.metadata?.prompt,
        title: node.title,
        taskId: node.metadata?.taskId,
        createdAt: node.metadata?.taskCompletedAt || new Date().toISOString(),
    };
}
