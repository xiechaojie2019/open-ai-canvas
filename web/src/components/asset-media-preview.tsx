import type { ReactNode } from "react";

import { CachedResourceImage } from "@/components/cached-resource-image";
import { resolveResourceUrl } from "@/services/api/resources";
import type { Asset } from "@/stores/use-asset-store";

type AssetMediaPreviewProps = {
    asset?: Asset | null;
    alt: string;
    className?: string;
    fallback?: ReactNode;
};

export function AssetMediaPreview({ asset, alt, className = "", fallback = null }: AssetMediaPreviewProps) {
    if (!asset) return fallback;

    if (asset.kind === "video") {
        const videoUrl = resolveResourceUrl(asset.data.storageKey, asset.data.url);
        if (!videoUrl) return fallback;
        const poster = asset.coverUrl && asset.coverUrl !== asset.data.url ? asset.coverUrl : undefined;
        return (
            <video
                src={videoUrl}
                poster={poster}
                aria-label={alt}
                muted
                playsInline
                preload="metadata"
                className={className}
                onLoadedMetadata={(event) => {
                    // 主动触发首帧附近的解码，避免只有 metadata 时长期停留在空白画面。
                    const video = event.currentTarget;
                    if (!poster && video.currentTime === 0 && video.duration > 0) video.currentTime = Math.min(0.001, video.duration);
                }}
            />
        );
    }

    const storageKey = asset.kind === "image" ? asset.data.storageKey : undefined;
    const imageUrl = asset.kind === "image" ? resolveResourceUrl(asset.data.storageKey, asset.data.dataUrl || asset.coverUrl) : asset.coverUrl;
    if (!imageUrl && !storageKey) return fallback;
    return <CachedResourceImage storageKey={storageKey} src={imageUrl} alt={alt} loading="lazy" decoding="async" className={className} fallback={fallback} />;
}
