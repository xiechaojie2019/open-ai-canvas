import { Modal } from "antd";
import { Check } from "lucide-react";

import { CachedResourceImage } from "@/components/cached-resource-image";
import { CanvasVideoPreviewImage } from "@/components/canvas/canvas-video-preview-image";
import { canvasThemes } from "@/lib/canvas-theme";
import { mediaHistoryEntries } from "@/lib/canvas/canvas-media-history";
import { useActiveTheme } from "@/stores/canvas/use-canvas-theme-store";
import { CanvasNodeType, type CanvasMediaHistoryEntry, type CanvasNodeData } from "@/types/canvas";

/**
 * 同一个生成节点历次结果的切换面板。
 *
 * 画布上同一个来源节点的多次生成不再各建一个节点，而是把旧结果压进
 * `metadata.mediaHistory`、节点只展示最新一张。这里把它们列出来供切换查看。
 * 选中某一项后由 `onSelect` 把该项与节点顶层的产物字段对调（见
 * `selectMediaHistoryVersion`）。
 */
export function CanvasMediaHistoryModal({
    open,
    node,
    onClose,
    onSelect,
}: {
    open: boolean;
    node?: CanvasNodeData;
    onClose: () => void;
    onSelect: (entryId: string) => void;
}) {
    const theme = canvasThemes[useActiveTheme()];
    const history = mediaHistoryEntries(node?.metadata);
    const current: CanvasMediaHistoryEntry | undefined = node
        ? {
              id: "__current__",
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
          }
        : undefined;
    const entries: { entry: CanvasMediaHistoryEntry; isCurrent: boolean }[] = [
        ...(current && (current.content || current.storageKey || current.videoPreview?.storageKey) ? [{ entry: current, isCurrent: true }] : []),
        ...history.map((entry) => ({ entry, isCurrent: false })),
    ];
    return (
        <Modal
            title={`历史记录（共 ${entries.length} 个结果）`}
            open={open}
            footer={null}
            centered
            width={Math.min(1180, Math.max(440, 112 + Math.min(entries.length, 4) * 340))}
            onCancel={onClose}
            styles={{ body: { overflow: "hidden" } }}
        >
            <div className="thin-scrollbar grid max-h-[70vh] grid-flow-col auto-cols-[328px] gap-3 overflow-x-auto pb-2">
                {entries.map(({ entry, isCurrent }, index) => (
                    <article
                        key={entry.id}
                        className="overflow-hidden rounded-[var(--r-lg)] border"
                        style={{
                            borderColor: isCurrent ? theme.accent.primary : theme.node.stroke,
                            background: theme.node.panel,
                            boxShadow: isCurrent ? "0 0 0 2px " + theme.accent.primarySoft : undefined,
                        }}
                    >
                        <div className="flex h-11 items-center justify-between gap-3 border-b px-3" style={{ borderColor: theme.node.stroke }}>
                            <div className="flex min-w-0 flex-1 items-center gap-2">
                                <span
                                    className="grid size-7 shrink-0 place-items-center rounded-[var(--r-full)] border text-[var(--node-badge-fs)] font-semibold leading-none"
                                    style={{
                                        background: isCurrent ? theme.accent.primarySoft : theme.toolbar.itemHover,
                                        borderColor: isCurrent ? theme.accent.primary : theme.node.stroke,
                                        color: isCurrent ? theme.accent.primary : theme.node.text,
                                    }}
                                >
                                    {entry.id === "__current__" ? "现" : entry.id.startsWith("hist-") ? `#${index}` : index}
                                </span>
                                <span className="min-w-0 truncate text-xs font-semibold text-[var(--fs-label)] whitespace-nowrap" style={{ color: theme.node.muted }}>
                                    {formatCreatedAt(entry.createdAt) || "——"}
                                </span>
                            </div>
                            {isCurrent ? (
                                <span className="inline-flex shrink-0 items-center gap-1 whitespace-nowrap text-[var(--fs-tiny)] font-medium" style={{ color: theme.accent.primary }}>
                                    <Check className="size-3" />
                                    当前展示
                                </span>
                            ) : null}
                        </div>
                        <button
                            type="button"
                            className="block h-52 w-full overflow-hidden"
                            style={{ background: theme.node.fill }}
                            disabled={isCurrent}
                            title={isCurrent ? "当前展示的结果" : "切换到这个结果"}
                            onClick={() => {
                                if (!isCurrent) onSelect(entry.id);
                            }}
                        >
                            {node?.type === CanvasNodeType.Video ? (
                                <CanvasVideoPreviewImage
                                    node={{ ...node, metadata: { ...node.metadata, ...entry } }}
                                    alt={entry.prompt || "历史视频"}
                                    className="size-full object-contain"
                                    loading="lazy"
                                    decoding="async"
                                    fallback={
                                        <span className="grid size-full place-items-center px-4 text-center text-xs" style={{ color: theme.node.muted }}>
                                            暂无视频首帧
                                        </span>
                                    }
                                />
                            ) : (
                                <CachedResourceImage
                                    storageKey={entry.storageKey || ""}
                                    src={entry.content || ""}
                                    alt={entry.prompt || "历史图片"}
                                    className="size-full object-contain"
                                    loading="lazy"
                                    decoding="async"
                                    fallback={
                                        <span className="grid size-full place-items-center px-4 text-center text-xs" style={{ color: theme.node.muted }}>
                                            暂无图片
                                        </span>
                                    }
                                />
                            )}
                        </button>
                        <div className="space-y-2 p-3 text-[var(--fs-label)]">
                            <Info label="尺寸" value={dimensionLabel(entry)} />
                            <div>
                                <div className="whitespace-nowrap opacity-45">提示词</div>
                                <div className="mt-1 line-clamp-5 whitespace-pre-wrap break-words leading-5">{entry.prompt || node?.metadata?.prompt || "未填写"}</div>
                            </div>
                            {!isCurrent ? (
                                <button
                                    type="button"
                                    className="w-full rounded-[var(--r-sm)] border px-3 py-1.5 text-[var(--fs-label)] font-medium transition-colors"
                                    style={{ borderColor: theme.node.stroke, color: theme.node.text }}
                                    onClick={() => onSelect(entry.id)}
                                >
                                    切换到这个结果
                                </button>
                            ) : null}
                        </div>
                    </article>
                ))}
            </div>
        </Modal>
    );
}

function dimensionLabel(entry: CanvasMediaHistoryEntry) {
    if (entry.naturalWidth && entry.naturalHeight) return `${entry.naturalWidth} × ${entry.naturalHeight}`;
    if (entry.durationMs) return `${Math.round(entry.durationMs / 1000)} 秒`;
    return "——";
}

function formatCreatedAt(value?: string) {
    if (!value) return "";
    const time = new Date(value);
    return Number.isNaN(time.getTime()) ? "" : time.toLocaleString();
}

function Info({ label, value }: { label: string; value: string }) {
    return (
        <div className="flex min-w-0 items-center gap-3">
            <span className="w-9 shrink-0 whitespace-nowrap opacity-45">{label}</span>
            <span className="min-w-0 flex-1 truncate text-right font-medium" title={value}>
                {value}
            </span>
        </div>
    );
}
