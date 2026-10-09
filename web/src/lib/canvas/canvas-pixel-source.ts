import { getMediaBlob } from "@/services/file-storage";
import { getImageBlob } from "@/services/image-storage";
import type { CanvasNodeData } from "@/types/canvas";

/**
 * 解析像素级操作（裁剪、切分、放大）可安全读取的图片来源。
 *
 * 这类操作必须先把图画到 canvas 再导出，所以图片必须**同源可读**：
 * 云端资源的展示地址是同源的 `/api/.../file`，浏览器会以 no-cors 跟随它
 * 307 跳到对象存储；一旦落到别的源，canvas 就被标记为跨域，`toDataURL`
 * 直接抛 SecurityError（表现为「切分失败：Tainted canvases may not be exported」）。
 *
 * 因此优先用本地媒体缓存里的 Blob 构造同源 objectURL，取不到才回退原始地址。
 * 回退是否安全取决于原始地址：跨源绝对地址仍会带 `crossOrigin` 正常走 CORS，
 * 而同源 `/api/...` 地址会 307 出去，仍然无法导出。
 */
export async function resolveCroppableImageSource(node: CanvasNodeData): Promise<{ url: string; release: () => void }> {
    const content = node.metadata?.content ?? "";
    if (content.startsWith("data:") || content.startsWith("blob:")) return { url: content, release: () => {} };
    const storageKey = node.metadata?.storageKey;
    if (!storageKey) return { url: content, release: () => {} };
    const readBlob = storageKey.startsWith("image:") || storageKey.startsWith("generation-image:") ? getImageBlob : getMediaBlob;
    const blob = await readBlob(storageKey).catch(() => null);
    if (!blob) return { url: content, release: () => {} };
    const url = URL.createObjectURL(blob);
    return { url, release: () => URL.revokeObjectURL(url) };
}
