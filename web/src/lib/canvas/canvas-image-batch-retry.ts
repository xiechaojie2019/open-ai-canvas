import { CanvasNodeType, type CanvasConnection, type CanvasMediaHistoryEntry, type CanvasNodeData, type CanvasNodeMetadata } from "@/types/canvas";
import { MEDIA_HISTORY_LIMIT } from "@/lib/canvas/canvas-media-history";
import { resetGenerationTaskMetadata } from "@/lib/canvas/canvas-task-state";

export function hasImageBatchResult(node: CanvasNodeData) {
    return Boolean(node.metadata?.content || node.metadata?.storageKey);
}

export function liveImageBatchChildren(root: CanvasNodeData, nodes: CanvasNodeData[]) {
    if (root.type !== CanvasNodeType.Image) return [];
    // 清理是破坏性写路径，父节点的旧索引不能证明子节点仍归这个批次所有。
    return nodes.filter((node) => node.id !== root.id && node.type === CanvasNodeType.Image && node.metadata?.batchRootId === root.id);
}

export function retireImageBatchChildren(root: CanvasNodeData, nodes: CanvasNodeData[], connections: CanvasConnection[]) {
    const children = liveImageBatchChildren(root, nodes);
    if (!children.length) return { nodes, connections, removedIds: [] as string[] };
    const removedIds = children.filter((node) => !hasImageBatchResult(node)).map((node) => node.id);
    const removed = new Set(removedIds);
    const kept = children.filter(hasImageBatchResult);
    const nextNodes = nodes
        .filter((node) => !removed.has(node.id))
        .map((node) => {
            if (node.id === root.id) {
                const metadata = { ...node.metadata, imageBatchExpanded: true };
                delete metadata.batchChildIds;
                delete metadata.primaryImageId;
                delete metadata.batchFailedCount;
                if (!hasImageBatchResult(node)) metadata.status = "idle";
                return { ...node, metadata };
            }
            const index = kept.findIndex((item) => item.id === node.id);
            if (index < 0) return node;
            const metadata = { ...node.metadata };
            delete metadata.batchRootId;
            return {
                ...node,
                position: { x: root.position.x - node.width - 48, y: root.position.y + index * (node.height + 24) },
                metadata,
            };
        });
    return {
        nodes: nextNodes,
        connections: connections.filter((connection) => !removed.has(connection.fromNodeId) && !removed.has(connection.toNodeId)),
        removedIds,
    };
}

export function cancelIncompleteImageBatch(rootId: string, childIds: string[], nodes: CanvasNodeData[], connections: CanvasConnection[]) {
    const childIdSet = new Set(childIds);
    const removedIds = nodes.filter((node) => childIdSet.has(node.id) && node.metadata?.batchRootId === rootId && !hasImageBatchResult(node)).map((node) => node.id);
    const removed = new Set(removedIds);
    const nextNodes = nodes
        .filter((node) => !removed.has(node.id))
        .map((node) => {
            if (node.id !== rootId) return node;
            const remaining = (node.metadata?.batchChildIds || []).filter((id) => !removed.has(id) && nodes.some((item) => item.id === id && item.metadata?.batchRootId === rootId && hasImageBatchResult(item)));
            const hasContent = hasImageBatchResult(node);
            const metadata = {
                ...node.metadata,
                batchChildIds: remaining.length ? remaining : undefined,
                isBatchRoot: remaining.length > 1 ? true : undefined,
                imageBatchExpanded: remaining.length > 1 ? true : undefined,
                status: hasContent ? node.metadata?.status : "idle",
            };
            if (!hasContent) {
                delete metadata.errorDetails;
                delete metadata.generationErrorCode;
                delete metadata.failedPromptFingerprint;
            }
            if (remaining.length <= 1) {
                delete metadata.batchFailedCount;
                delete metadata.primaryImageId;
                delete metadata.isBatchRoot;
                delete metadata.imageBatchExpanded;
                delete metadata.batchChildIds;
            }
            return { ...node, metadata };
        });
    const remainingIds = new Set(
        nextNodes.find((node) => node.id === rootId)?.metadata?.batchChildIds || [],
    );
    const detached = nextNodes.map((node) => {
        if (!childIdSet.has(node.id) || node.metadata?.batchRootId !== rootId || remainingIds.has(node.id)) return node;
        const metadata = { ...node.metadata };
        delete metadata.batchRootId;
        return { ...node, metadata };
    });
    return {
        nodes: detached,
        connections: connections.filter((connection) => !removed.has(connection.fromNodeId) && !removed.has(connection.toNodeId)),
        removedIds,
    };
}

/**
 * 批次生成结束后默认折叠成一张主图：其余成功结果折进节点历史（可切换查看）。
 *
 * 子节点与连线都保留不删，只把 `imageBatchExpanded` 置 false —— 想并排比对时
 * 仍然可以用批次按钮「展开对比」。因此这里刻意不放进 `reconcileImageBatchRoot`：
 * 那个函数也会在画布加载/任务同步时被调用，会把手动展开的批次又收起来。
 */
export function collapseCompletedImageBatch(root: CanvasNodeData, nodes: CanvasNodeData[]) {
    if (root.type !== CanvasNodeType.Image || !root.metadata?.isBatchRoot) return root;
    const nodeById = new Map(nodes.map((node) => [node.id, node]));
    const children = (root.metadata.batchChildIds || [])
        .map((id) => nodeById.get(id))
        .filter((node): node is CanvasNodeData => Boolean(node && node.type === CanvasNodeType.Image && node.metadata?.batchRootId === root.id));
    // 还有在途子任务时不折叠，免得生成过程看不到进度。
    if (!children.length || children.some((node) => node.metadata?.status === "loading")) return root;
    const primaryId = root.metadata.primaryImageId;
    const extras = children.filter((node) => node.id !== primaryId && hasImageBatchResult(node));
    const metadata: CanvasNodeMetadata = { ...root.metadata, imageBatchExpanded: false };
    if (extras.length) metadata.mediaHistory = mergeBatchMediaHistory(root.metadata, extras);
    return { ...root, metadata };
}

/** 把批次子结果折进历史（最新在前，按 storageKey 去重，重复调用幂等）。 */
function mergeBatchMediaHistory(metadata: CanvasNodeMetadata | undefined, children: CanvasNodeData[]): CanvasMediaHistoryEntry[] {
    const existing = Array.isArray(metadata?.mediaHistory) ? metadata.mediaHistory : [];
    const known = new Set(existing.map((entry) => entry.storageKey).filter((key): key is string => Boolean(key)));
    const fresh = children
        .map((node) => batchMediaHistoryEntry(node))
        .filter((entry) => Boolean(entry.storageKey) && !known.has(entry.storageKey as string));
    // 同一批里后出的排前面，与「最新在前」约定一致。
    return [...fresh.reverse(), ...existing].slice(0, MEDIA_HISTORY_LIMIT);
}

function batchMediaHistoryEntry(node: CanvasNodeData): CanvasMediaHistoryEntry {
    return {
        // 用子节点 id 派生，重复调用不会产生重复条目。
        id: `hist-${node.id}`,
        content: node.metadata?.content,
        storageKey: node.metadata?.storageKey,
        previewContent: node.metadata?.previewContent,
        videoPreview: node.metadata?.videoPreview,
        naturalWidth: node.metadata?.naturalWidth,
        naturalHeight: node.metadata?.naturalHeight,
        bytes: node.metadata?.bytes,
        mimeType: node.metadata?.mimeType,
        durationMs: node.metadata?.durationMs,
        prompt: node.metadata?.prompt,
        title: node.title,
        taskId: node.metadata?.taskId,
        createdAt: node.metadata?.taskCompletedAt || new Date().toISOString(),
    };
}

export function failedImageBatchChildren(root: CanvasNodeData, nodes: CanvasNodeData[]) {
    if (root.type !== CanvasNodeType.Image || !root.metadata?.isBatchRoot) return [];
    const nodeById = new Map(nodes.map((node) => [node.id, node]));
    return (root.metadata.batchChildIds || [])
        .map((id) => nodeById.get(id))
        .filter((node): node is CanvasNodeData => Boolean(node && node.type === CanvasNodeType.Image && node.metadata?.batchRootId === root.id && node.metadata.status === "error"));
}

export function markImageBatchRetrying(rootId: string, childIds: string[], nodes: CanvasNodeData[]): CanvasNodeData[] {
    const retryingIds = new Set(childIds);
    return nodes.map((node) => {
        if (node.id !== rootId && !retryingIds.has(node.id)) return node;
        return {
            ...node,
            metadata: {
                ...node.metadata,
                status: "loading",
                ...(node.id === rootId ? { batchFailedCount: childIds.length } : {}),
                errorDetails: undefined,
                generationErrorCode: undefined,
                resourceReloadAvailable: undefined,
                failedPromptFingerprint: undefined,
            },
        };
    });
}

export function restoreUnsubmittedImageBatchChild(current: CanvasNodeData, original: CanvasNodeData): CanvasNodeData {
    if (current.metadata?.status !== "loading") return current;
    return {
        ...current,
        metadata: {
            ...current.metadata,
            status: "error",
            errorDetails: original.metadata?.errorDetails || "重试请求未提交",
            generationErrorCode: original.metadata?.generationErrorCode,
            resourceReloadAvailable: original.metadata?.resourceReloadAvailable,
            failedPromptFingerprint: original.metadata?.failedPromptFingerprint,
        },
    };
}

export function reconcileImageBatchRoot(root: CanvasNodeData, nodes: CanvasNodeData[]) {
    if (root.type !== CanvasNodeType.Image || !root.metadata?.isBatchRoot) return root;
    const nodeById = new Map(nodes.map((node) => [node.id, node]));
    const children = (root.metadata.batchChildIds || [])
        .map((id) => nodeById.get(id))
        .filter((node): node is CanvasNodeData => Boolean(node && node.type === CanvasNodeType.Image && node.metadata?.batchRootId === root.id));
    if (!children.length) {
        const metadata: CanvasNodeMetadata = resetGenerationTaskMetadata(root.metadata, root.metadata?.status);
        delete metadata.batchChildIds;
        delete metadata.batchFailedCount;
        delete metadata.primaryImageId;
        delete metadata.isBatchRoot;
        delete metadata.imageBatchExpanded;
        delete metadata.errorDetails;
        delete metadata.generationErrorCode;
        delete metadata.resourceReloadAvailable;
        delete metadata.failedPromptFingerprint;
        if (!hasImageBatchResult(root)) metadata.status = "idle";
        return { ...root, metadata };
    }

    const primary = children.find((node) => node.id === root.metadata?.primaryImageId && hasImageBatchResult(node)) || children.find(hasImageBatchResult);
    const loading = children.some((node) => node.metadata?.status === "loading");
    const failed = children.find((node) => node.metadata?.status === "error");
    // 批次父节点只是子任务的投影，不能保留上一轮单图任务的身份/终态。
    const metadata: CanvasNodeMetadata = resetGenerationTaskMetadata(root.metadata, root.metadata?.status);
    metadata.batchFailedCount = children.filter((node) => node.metadata?.status === "error").length;

    if (primary) {
        metadata.content = primary.metadata?.content;
        metadata.storageKey = primary.metadata?.storageKey;
        metadata.assetId = primary.metadata?.assetId;
        metadata.mimeType = primary.metadata?.mimeType;
        metadata.bytes = primary.metadata?.bytes;
        metadata.naturalWidth = primary.metadata?.naturalWidth;
        metadata.naturalHeight = primary.metadata?.naturalHeight;
        metadata.primaryImageId = primary.id;
        metadata.status = "success";
        delete metadata.errorDetails;
        delete metadata.generationErrorCode;
        delete metadata.failedPromptFingerprint;
    } else {
        delete metadata.content;
        delete metadata.storageKey;
        delete metadata.assetId;
        delete metadata.mimeType;
        delete metadata.bytes;
        delete metadata.naturalWidth;
        delete metadata.naturalHeight;
        delete metadata.primaryImageId;
        metadata.status = loading ? "loading" : failed ? "error" : "idle";
        metadata.errorDetails = failed?.metadata?.errorDetails;
        metadata.generationErrorCode = failed?.metadata?.generationErrorCode;
        metadata.resourceReloadAvailable = failed?.metadata?.resourceReloadAvailable;
        metadata.failedPromptFingerprint = failed?.metadata?.failedPromptFingerprint;
    }

    return { ...root, metadata };
}
