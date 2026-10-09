import { afterAll, afterEach, beforeEach, describe, expect, test } from "bun:test";
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

// 宫格切分曾报「切分失败：Tainted canvases may not be exported」。
// 成因是像素级操作把 `node.metadata.content`（同源的 /api/.../file）直接交给 canvas：
// 浏览器以 no-cors 跟随服务端 307 跳到对象存储后，canvas 被判跨域，导出即抛 SecurityError。
// 这里锁两件事：① 三个像素级入口都必须先取同源来源；② 取来源的规则本身正确。

function compactSource(source: string) {
    return source.replace(/\s+/g, " ").trim();
}

function sourceSection(source: string, startMarker: string, endMarker: string) {
    const start = source.indexOf(startMarker);
    const end = source.indexOf(endMarker, start + startMarker.length);
    expect(start).toBeGreaterThanOrEqual(0);
    expect(end).toBeGreaterThan(start);
    return compactSource(source.slice(start, end));
}

describe("pixel operations require a same-origin image source", () => {
    const mediaTools = () =>
        Bun.file(new URL("../src/pages/canvas/use-canvas-media-tools.ts", import.meta.url)).text();

    const pixelOperations: Array<[string, string, string, string]> = [
        ["cropImageNode", "const cropImageNode = useCallback(", "const saveAnnotatedImageNode", "cropDataUrl"],
        ["splitImageNode", "const splitImageNode = useCallback(", "const maskEditImageNode", "splitDataUrl"],
        ["upscaleImageNode", "const upscaleImageNode = useCallback(", "const generateAngleNode", "upscaleDataUrl"],
    ];

    for (const [name, startMarker, endMarker, helper] of pixelOperations) {
        test(`${name} reads a same-origin source instead of the raw content url`, async () => {
            const section = sourceSection(await mediaTools(), startMarker, endMarker);
            expect(section).toContain("resolveCroppableImageSource(node)");
            expect(section).toContain("releaseSource");
            // 回归点：一旦有人再写成 `${helper}(node.metadata.content` 就会立刻污染画布。
            expect(section).not.toContain(`${helper}(node.metadata.content`);
        });
    }

    test("the resolver lives in one shared module, not duplicated per call site", async () => {
        const source = await mediaTools();
        expect(source).toContain('from "@/lib/canvas/canvas-pixel-source"');
        expect(source).not.toContain("async function resolveCroppableImageSource");
    });
});

// 用临时改写模块隔离存储层，只验证「取到的地址是否同源」这条契约。
const dir = mkdtempSync(join(tmpdir(), "canvas-pixel-source-"));
const root = new URL("../src/", import.meta.url);

const imageStoragePath = join(dir, "image-storage.ts");
const mediaStoragePath = join(dir, "file-storage.ts");
writeFileSync(imageStoragePath, [
    "export const blobs = new Map();",
    "export const getImageBlob = async (key) => {",
    '    if (key === "boom") throw new Error("read failed");',
    "    return blobs.get(key) ?? null;",
    "};",
].join("\n"));
writeFileSync(mediaStoragePath, [
    "export const blobs = new Map();",
    "export const getMediaBlob = async (key) => {",
    '    if (key === "boom") throw new Error("read failed");',
    "    return blobs.get(key) ?? null;",
    "};",
].join("\n"));

const modulePath = join(dir, "canvas-pixel-source.ts");
writeFileSync(modulePath, readFileSync(new URL("lib/canvas/canvas-pixel-source.ts", root), "utf8")
    .replace('"@/services/file-storage"', JSON.stringify(mediaStoragePath))
    .replace('"@/services/image-storage"', JSON.stringify(imageStoragePath)));

const pixels: typeof import("../src/lib/canvas/canvas-pixel-source") = await import(modulePath);
const imageStorage = await import(imageStoragePath);
const mediaStorage = await import(mediaStoragePath);

type ResolverNode = Parameters<typeof pixels.resolveCroppableImageSource>[0];
const node = (metadata: Record<string, unknown>) =>
    ({ id: "node-1", type: "image", title: "图", position: { x: 0, y: 0 }, width: 10, height: 10, metadata }) as unknown as ResolverNode;

const createObjectURL = URL.createObjectURL;
const revokeObjectURL = URL.revokeObjectURL;
let made: string[] = [];
let revoked: string[] = [];

beforeEach(() => {
    imageStorage.blobs.clear();
    mediaStorage.blobs.clear();
    made = [];
    revoked = [];
    let counter = 0;
    URL.createObjectURL = ((blob: Blob) => {
        made.push(String(blob.size));
        counter += 1;
        return `blob:test/${counter}`;
    }) as typeof URL.createObjectURL;
    URL.revokeObjectURL = ((url: string) => {
        revoked.push(url);
    }) as typeof URL.revokeObjectURL;
});

afterEach(() => {
    URL.createObjectURL = createObjectURL;
    URL.revokeObjectURL = revokeObjectURL;
});

afterAll(() => {
    rmSync(dir, { recursive: true, force: true });
});

describe("resolveCroppableImageSource", () => {
    test("resource backed images resolve to an object url, never the redirecting api path", async () => {
        imageStorage.blobs.set("resource:r1", new Blob(["png"], { type: "image/png" }));
        const source = await pixels.resolveCroppableImageSource(node({ content: "/api/resources/r1/file", storageKey: "resource:r1" }));
        expect(source.url).toBe("blob:test/1");
        expect(source.url).not.toContain("/api/resources/");
        source.release();
        expect(revoked).toEqual(["blob:test/1"]);
    });

    test("generation image keys read from the image store", async () => {
        imageStorage.blobs.set("generation-image:u:1", new Blob(["png"], { type: "image/png" }));
        const source = await pixels.resolveCroppableImageSource(node({ content: "/api/resources/r9/file", storageKey: "generation-image:u:1" }));
        expect(source.url).toBe("blob:test/1");
        expect(source.release).toBeFunction();
    });

    test("data and blob urls stay as they are", async () => {
        for (const content of ["data:image/png;base64,AAA", "blob:https://app/1"]) {
            const source = await pixels.resolveCroppableImageSource(node({ content, storageKey: "resource:r2" }));
            expect(source.url).toBe(content);
        }
        expect(made).toHaveLength(0);
    });

    test("falls back to the raw content when no storage key or blob is available", async () => {
        const noKey = await pixels.resolveCroppableImageSource(node({ content: "/api/resources/r3/file" }));
        expect(noKey.url).toBe("/api/resources/r3/file");

        const missing = await pixels.resolveCroppableImageSource(node({ content: "/api/resources/r4/file", storageKey: "resource:r4" }));
        expect(missing.url).toBe("/api/resources/r4/file");
    });

    test("a failed read degrades to the raw content instead of rejecting", async () => {
        const source = await pixels.resolveCroppableImageSource(node({ content: "/api/resources/r5/file", storageKey: "boom" }));
        expect(source.url).toBe("/api/resources/r5/file");
    });

    test("media keys read from the media store", async () => {
        mediaStorage.blobs.set("media:m1", new Blob(["png"], { type: "image/png" }));
        const source = await pixels.resolveCroppableImageSource(node({ content: "/api/resources/r6/file", storageKey: "media:m1" }));
        expect(source.url).toBe("blob:test/1");
    });
});
