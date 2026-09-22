/** Read actual dimensions from a playable video; never substitute display defaults for asset metadata. */
export function readVideoSize(source: string, signal?: AbortSignal): Promise<{ width: number; height: number }> {
    return new Promise((resolve, reject) => {
        if (signal?.aborted) {
            reject(new DOMException("The operation was aborted", "AbortError"));
            return;
        }
        const video = document.createElement("video");
        let settled = false;
        const cleanup = () => {
            clearTimeout(timer);
            signal?.removeEventListener("abort", onAbort);
            video.onloadedmetadata = null;
            video.onerror = null;
            video.removeAttribute("src");
            video.load();
        };
        const fail = (error: Error) => {
            if (settled) return;
            settled = true;
            cleanup();
            reject(error);
        };
        const onAbort = () => fail(new DOMException("The operation was aborted", "AbortError"));
        const timer = setTimeout(() => fail(new Error("视频尺寸读取超时，请确认视频可播放")), 15_000);
        signal?.addEventListener("abort", onAbort, { once: true });
        video.onloadedmetadata = () => {
            const { videoWidth: width, videoHeight: height } = video;
            if (!Number.isFinite(width) || !Number.isFinite(height) || width <= 0 || height <= 0) {
                fail(new Error("无法读取视频实际尺寸"));
                return;
            }
            if (settled) return;
            settled = true;
            cleanup();
            resolve({ width, height });
        };
        video.onerror = () => fail(new Error("无法读取视频尺寸，请确认格式受浏览器支持且资源可访问"));
        video.preload = "metadata";
        video.src = source;
        video.load();
    });
}
