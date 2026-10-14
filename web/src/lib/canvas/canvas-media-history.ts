import type { CanvasMediaHistoryEntry, CanvasNodeMetadata } from "@/types/canvas";

/**
 * 同一生成来源的产物个数上限。超出后丢弃最旧的一条，避免节点元数据无限膨胀
 * （画布文档是整体 PUT 的 JSON，历史条目会直接放大每次保存的体积）。
 */
export const MEDIA_HISTORY_LIMIT = 30;

/** 当前展示的产物字段：切换历史版本时与历史条目对调的正是这些字段。 */
const MEDIA_FIELDS = ["content", "storageKey", "previewContent", "videoPreview", "naturalWidth", "naturalHeight", "bytes", "mimeType", "durationMs"] as const;

/**
 * 生成来源 key：同一画布内 key 相同的多次生成都归并到同一个结果节点。
 *
 * 归并只认「基于哪张图」：**有参考图就以参考图集合为准**——同一张图不管走手动生成
 * 还是 Agent 生成，都会落到同一个节点上；没有参考图（文生图）才回落到来源节点。
 * 生成方式与来源节点本身不再参与 key（此前手动生成与 Agent 生成因为来源节点段不同，
 * 被拆成了两个节点）。
 *
 * 规则必须与后端 `CloudAgentMediaAggregation.SourceKey` **逐字一致**，
 * 否则前端手动生成与 Agent 生成会各自归并到不同的节点上。
 */
export function canvasMediaSourceKey(mode: string, sourceNodeId: string, referenceNodeIds: readonly string[]): string {
    const refs = [...new Set(referenceNodeIds.filter((id) => Boolean(id)))].sort();
    const anchor = refs.length ? refs.join(",") : sourceNodeId || "-";
    return ["v1", mode, anchor].join("|");
}

/**
 * 从 key 里取出「媒体大类 + 归并锚点」，忽略来源节点段。
 *
 * 用于兼容旧格式（`v1|mode|sourceNodeId|refs` 四段）与跨路径匹配：老画布上的节点写的
 * 是旧 key，只比整串会永远匹配不上，同一张图于是又长出一个新节点。
 */
function canvasMediaSourceIdentity(key: string): string {
    const parts = key.split("|");
    if (parts.length === 4 && parts[0] === "v1") {
        const refs = parts[3];
        return [parts[1], refs && refs !== "-" ? refs : parts[2] || "-"].join("|");
    }
    if (parts.length === 3 && parts[0] === "v1") {
        return [parts[1], parts[2] || "-"].join("|");
    }
    return key;
}

/** 节点是否承载了「来源标记」——只有带标记的节点才会被复用为归并目标。 */
export function isMediaAggregationTarget(metadata: CanvasNodeMetadata | undefined, sourceKey: string): boolean {
    const target = metadata?.generationSourceKey;
    if (!target || !sourceKey) return false;
    if (target === sourceKey) return true;
    return canvasMediaSourceIdentity(target) === canvasMediaSourceIdentity(sourceKey);
}

export function mediaHistoryEntries(metadata: CanvasNodeMetadata | undefined): CanvasMediaHistoryEntry[] {
    const history = metadata?.mediaHistory;
    return Array.isArray(history) ? history : [];
}

/** 节点当前是否有可展示的产物（用于决定要不要把顶层字段压进历史）。 */
export function hasCurrentMedia(metadata: CanvasNodeMetadata | undefined): boolean {
    return Boolean(metadata?.storageKey) || Boolean(metadata?.content) || Boolean(metadata?.videoPreview?.storageKey);
}

/**
 * 把节点当前展示的产物压进历史（最新在前），并从顶层字段移除。
 *
 * 幂等：顶层没有产物时原样返回，因此「建草稿」与「提交任务」两次调用只会入栈一次。
 * 同一条产物重复入栈时（例如回写重试）会被识别并跳过。
 */
export function pushCurrentMediaToHistory(
    metadata: CanvasNodeMetadata | undefined,
    extra?: { id: string; taskId?: string; title?: string; createdAt?: string },
): CanvasNodeMetadata {
    const next: CanvasNodeMetadata = { ...metadata };
    if (!hasCurrentMedia(metadata)) return next;
    const entry: CanvasMediaHistoryEntry = {
        id: extra?.id || `hist-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`,
        content: metadata?.content,
        storageKey: metadata?.storageKey,
        previewContent: metadata?.previewContent,
        videoPreview: metadata?.videoPreview,
        naturalWidth: metadata?.naturalWidth,
        naturalHeight: metadata?.naturalHeight,
        bytes: metadata?.bytes,
        mimeType: metadata?.mimeType,
        durationMs: metadata?.durationMs,
        prompt: metadata?.prompt,
        title: extra?.title,
        taskId: extra?.taskId ?? metadata?.taskId,
        createdAt: extra?.createdAt ?? new Date().toISOString(),
    };
    const previous = mediaHistoryEntries(metadata);
    const duplicated = previous.length > 0 && Boolean(entry.storageKey) && previous[0].storageKey === entry.storageKey;
    next.mediaHistory = (duplicated ? previous : [entry, ...previous]).slice(0, MEDIA_HISTORY_LIMIT);
    for (const field of MEDIA_FIELDS) delete next[field];
    return next;
}

/** 把选中的历史条目切换为「当前展示」，原当前产物回到历史中原位置。 */
export function selectMediaHistoryVersion(metadata: CanvasNodeMetadata | undefined, entryId: string): CanvasNodeMetadata {
    const history = mediaHistoryEntries(metadata);
    const index = history.findIndex((entry) => entry.id === entryId);
    if (index < 0) return { ...metadata };
    const selected = history[index];
    const swap: CanvasMediaHistoryEntry = {
        id: `hist-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`,
        content: metadata?.content,
        storageKey: metadata?.storageKey,
        previewContent: metadata?.previewContent,
        videoPreview: metadata?.videoPreview,
        naturalWidth: metadata?.naturalWidth,
        naturalHeight: metadata?.naturalHeight,
        bytes: metadata?.bytes,
        mimeType: metadata?.mimeType,
        durationMs: metadata?.durationMs,
        prompt: metadata?.prompt,
        taskId: metadata?.taskId,
        createdAt: new Date().toISOString(),
    };
    const next: CanvasNodeMetadata = {
        ...metadata,
        content: selected.content,
        storageKey: selected.storageKey,
        previewContent: selected.previewContent,
        videoPreview: selected.videoPreview,
        naturalWidth: selected.naturalWidth,
        naturalHeight: selected.naturalHeight,
        bytes: selected.bytes,
        mimeType: selected.mimeType,
        durationMs: selected.durationMs,
        mediaHistory: [...history.slice(0, index), swap, ...history.slice(index + 1)],
        // 切换只是换展示，不重新生成：状态回到成功，避免停在旧的 loading/error 上。
        status: hasCurrentMedia(selected) ? "success" : metadata?.status,
    };
    for (const field of MEDIA_FIELDS) {
        if (next[field] === undefined) delete next[field];
    }
    return next;
}
