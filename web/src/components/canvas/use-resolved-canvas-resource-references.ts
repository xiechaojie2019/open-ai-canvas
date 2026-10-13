import { useCallback, useEffect, useMemo, useRef, useState } from "react";

import type { CanvasResourceReference } from "@/lib/canvas/canvas-resource-references";
import { loadCanvasDrawingPreview } from "@/lib/canvas/canvas-drawing-storage";
import { resolveImageUrl } from "@/services/image-storage";

type ResolvedPreview = {
    identity: string;
    url: string;
};

type PreviewCacheEntry = {
    promise: Promise<string>;
    expiresAt: number;
};

/**
 * 引用预览地址是短签名地址：本地资源 display 与云资源 OSS 直链都只有 5 分钟寿命，
 * 后端 refreshAt 是它的 80%。缓存必须早于签名失效就重新解析，否则长驻面板里的引用
 * chip、以及重新挂载的节点会一直复用过期地址：<img> 收到 403 只能显示裂图，且只有
 * 整页刷新（内存缓存清空后重新签发）才会恢复。
 */
const PREVIEW_URL_TTL_MS = 4 * 60 * 1000;

const previewPromiseCache = new Map<string, PreviewCacheEntry>();

export function useResolvedCanvasResourceReferences(references: CanvasResourceReference[], options?: { projectId?: string }) {
    const projectId = options?.projectId;
    const requests = useMemo(
        () => references.flatMap((reference) => {
            const identity = previewIdentity(reference, projectId);
            return identity ? [{ reference, identity }] : [];
        }),
        [projectId, references],
    );
    const [resolvedById, setResolvedById] = useState<Record<string, ResolvedPreview>>({});
    const [revision, setRevision] = useState(0);
    const refreshTimerRef = useRef<number | null>(null);

    // 请求列表每次渲染都会换新数组，所以定时器只能在没有的时候挂一个：
    // 否则高频渲染的提示词面板会不断重置定时器，换签永远不会发生。
    const ensureRefreshTimer = useCallback(() => {
        if (refreshTimerRef.current !== null || typeof window === "undefined") return;
        refreshTimerRef.current = window.setTimeout(() => {
            refreshTimerRef.current = null;
            setRevision((value) => value + 1);
        }, PREVIEW_URL_TTL_MS);
    }, []);

    useEffect(() => () => {
        if (refreshTimerRef.current !== null) {
            window.clearTimeout(refreshTimerRef.current);
            refreshTimerRef.current = null;
        }
    }, []);

    useEffect(() => {
        if (!requests.length) return;
        ensureRefreshTimer();
        let cancelled = false;
        void Promise.all(
            requests.map(async ({ reference, identity }) => ({
                id: reference.id,
                identity,
                url: await resolveReferencePreview(reference, identity, projectId),
            })),
        ).then((resolved) => {
            if (cancelled) return;
            setResolvedById((current) => {
                let changed = false;
                const next = { ...current };
                resolved.forEach(({ id, identity, url }) => {
                    if (!url || (current[id]?.identity === identity && current[id]?.url === url)) return;
                    next[id] = { identity, url };
                    changed = true;
                });
                return changed ? next : current;
            });
        });
        return () => {
            cancelled = true;
        };
    }, [ensureRefreshTimer, projectId, requests, revision]);

    return useMemo(
        () => references.map((reference) => {
            const identity = previewIdentity(reference, projectId);
            const resolved = identity ? resolvedById[reference.id] : undefined;
            return resolved?.identity === identity && resolved.url !== reference.previewUrl ? { ...reference, previewUrl: resolved.url } : reference;
        }),
        [projectId, references, resolvedById],
    );
}

function previewIdentity(reference: CanvasResourceReference, projectId?: string) {
    // 只有当 drawingId 和 projectId 同时存在时才返回绘图身份,避免不完整的缓存键导致运行时错误
    if (reference.drawingId) {
        if (!projectId) return "";
        return `drawing:${projectId}:${reference.drawingId}:${reference.drawingRevision || 0}`;
    }
    const storageKey = reference.kind === "video" ? reference.previewStorageKey : reference.storageKey;
    if (!storageKey || !["image", "video", "character"].includes(reference.kind)) return "";
    return `${reference.kind}:${storageKey}`;
}

function resolveReferencePreview(reference: CanvasResourceReference, identity: string, projectId?: string) {
    const cached = previewPromiseCache.get(identity);
    if (cached && cached.expiresAt > Date.now()) return cached.promise;
    previewPromiseCache.delete(identity);
    const entry: PreviewCacheEntry = { promise: Promise.resolve(""), expiresAt: Date.now() + PREVIEW_URL_TTL_MS };
    if (reference.drawingId && projectId) {
        // 绘图预览是从 IndexedDB 读出来的 data URL，不会过期，无需按时换签。
        entry.expiresAt = Number.POSITIVE_INFINITY;
        entry.promise = loadCanvasDrawingPreview(projectId, reference.drawingId)
            .then((preview) => preview ? blobToDataUrl(preview) : reference.previewUrl || "")
            .catch(() => reference.previewUrl || "")
            .then((url) => {
                if (!url && previewPromiseCache.get(identity) === entry) previewPromiseCache.delete(identity);
                return url;
            });
    } else {
        const storageKey = reference.kind === "video" ? reference.previewStorageKey : reference.storageKey;
        entry.promise = resolveImageUrl(storageKey, reference.previewUrl || "", { cacheMiss: true })
            .catch(() => reference.previewUrl || "")
            .then((url) => {
                if (!url && previewPromiseCache.get(identity) === entry) previewPromiseCache.delete(identity);
                return url;
            });
    }
    previewPromiseCache.set(identity, entry);
    return entry.promise;
}

function blobToDataUrl(blob: Blob) {
    return new Promise<string>((resolve, reject) => {
        const reader = new FileReader();
        reader.onerror = () => reject(reader.error || new Error("读取绘图预览失败"));
        reader.onload = () => resolve(typeof reader.result === "string" ? reader.result : "");
        reader.readAsDataURL(blob);
    });
}
