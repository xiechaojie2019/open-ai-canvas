import { afterEach, beforeEach, expect, test } from "bun:test";
import localforage from "localforage";

import { apiClient } from "../src/services/api/request";
import { ensureCanvasNodeAsset } from "../src/services/project-asset-sync";
import { flushAssetStorePersistence, useAssetStore, type Asset } from "../src/stores/use-asset-store";
import { flushCanvasStorePersistence, useCanvasStore, type CanvasProject } from "../src/stores/canvas/use-canvas-store";
import { initializeRemoteUserDataSession, repairUnsyncedCanvasTextAssets, resetRemoteUserDataSync } from "../src/services/user-data-sync";
import { CanvasNodeType, type CanvasNodeData } from "../src/types/canvas";

const originalWindow = globalThis.window;
const originalAdapter = apiClient.defaults.adapter;
const originalGet = localforage.getItem;
const originalSet = localforage.setItem;
const indexed = new Map<string, string>();
let remote = new Map<string, Asset>();
let requests: Array<{ method: string; url: string; asset?: Asset }> = [];
let failPut: ((asset: Asset) => boolean) | undefined;
let scope = "";
let sequence = 0;

function textNode(content: string, id = "text-node"): CanvasNodeData {
    return {
        id,
        type: CanvasNodeType.Text,
        title: "卖点信息",
        position: { x: 0, y: 0 },
        width: 320,
        height: 200,
        metadata: { content },
    };
}

function canvas(nodes: CanvasNodeData[]): CanvasProject {
    return {
        id: "canvas-1",
        revision: 1,
        title: "画布",
        createdAt: "2026-10-01T00:00:00Z",
        updatedAt: "2026-10-01T00:00:00Z",
        nodes,
        connections: [],
        chatSessions: [],
        activeChatId: null,
        viewport: { x: 0, y: 0, k: 1 },
    };
}

beforeEach(async () => {
    resetRemoteUserDataSync();
    scope = `asset-save-test-${++sequence}`;
    indexed.clear();
    remote = new Map();
    requests = [];
    failPut = undefined;
    Object.defineProperty(globalThis, "window", {
        configurable: true,
        value: {
            setTimeout: () => 1,
            clearTimeout: () => undefined,
            localStorage: { getItem: () => scope, setItem: () => undefined },
        },
    });
    localforage.getItem = (async (key: string) => indexed.get(key) ?? null) as typeof localforage.getItem;
    localforage.setItem = (async (key: string, value: string) => {
        indexed.set(key, value);
        return value;
    }) as typeof localforage.setItem;
    apiClient.defaults.adapter = async (config) => {
        const method = (config.method || "get").toLowerCase();
        const url = String(config.url);
        const body = config.data ? JSON.parse(config.data) : undefined;
        if (url === "/assets/batch" && method === "post") {
            requests.push({ method, url });
            const ids = new Set<string>(body.ids);
            return envelope(config, { assets: [...remote.values()].filter((asset) => ids.has(asset.id)) });
        }
        if (url.startsWith("/assets/") && method === "put") {
            const asset = body.asset as Asset;
            requests.push({ method, url, asset });
            if (failPut?.(asset)) return envelope(config, null, 500, "素材服务暂不可用");
            remote.set(asset.id, structuredClone(asset));
            return envelope(config, { asset });
        }
        if (url === "/user-data/snapshot") {
            return envelope(config, { assets: [...remote.values()], projects: structuredClone(useCanvasStore.getState().projects) });
        }
        if (url === "/assets" && method === "get") {
            return envelope(config, { assets: [...remote.values()], total: remote.size, page: 1, pageSize: 20, kindCounts: {}, categoryCounts: {}, folderCounts: {}, hasMore: false });
        }
        requests.push({ method, url });
        return envelope(config, {});
    };
    useCanvasStore.setState({ projects: [] });
    useAssetStore.setState({ assets: [] });
});

afterEach(async () => {
    resetRemoteUserDataSync();
    await Promise.all([flushCanvasStorePersistence(), flushAssetStorePersistence()]);
    apiClient.defaults.adapter = originalAdapter;
    localforage.getItem = originalGet;
    localforage.setItem = originalSet;
    if (originalWindow === undefined) delete (globalThis as { window?: unknown }).window;
    else Object.defineProperty(globalThis, "window", { configurable: true, value: originalWindow });
});

function envelope(config: unknown, data: unknown, code = 0, msg = "ok") {
    return { config, headers: {}, status: code === 0 ? 200 : code, statusText: "", data: { code, data, msg } };
}

test("新建文本素材在返回成功前写入远端素材库", async () => {
    const node = textNode("这是一段卖点文案");
    useCanvasStore.setState({ projects: [canvas([node])] });
    await initializeRemoteUserDataSession(scope);

    const result = await ensureCanvasNodeAsset({ canvasId: "canvas-1", node, source: "canvas-manual" });

    const puts = requests.filter((request) => request.method === "put");
    expect(puts).toHaveLength(1);
    expect(puts[0].url).toBe(`/assets/${result.assetId}`);
    expect(puts[0].asset).toMatchObject({ id: result.assetId, kind: "text", data: { content: "这是一段卖点文案" } });
    expect(remote.get(result.assetId)?.data).toEqual({ content: "这是一段卖点文案" });
});

test("远端写入失败时撤回本地素材，不留下素材库查不到的记录", async () => {
    const node = textNode("上传会失败的文本");
    failPut = () => true;
    await initializeRemoteUserDataSession(scope);

    await expect(ensureCanvasNodeAsset({ canvasId: "canvas-1", node, source: "canvas-manual" })).rejects.toThrow("素材服务暂不可用");

    expect(useAssetStore.getState().assets).toEqual([]);
    expect(remote.size).toBe(0);
});

test("旧版本遗留的幽灵文本在登录后补传，远端已有记录不回写旧正文", async () => {
    const ghost: Asset = {
        id: "ghost-text", kind: "text", title: "旧文本", coverUrl: "", tags: [],
        createdAt: "2026-10-01T00:00:00.000Z", updatedAt: "2026-10-01T00:00:00.000Z",
        data: { content: "旧版本写入失败但留在本地的文本" },
    } as Asset;
    const fresh: Asset = {
        id: "fresh-text", kind: "text", title: "远端较新", coverUrl: "", tags: [],
        createdAt: "2026-09-01T00:00:00.000Z", updatedAt: "2026-09-01T00:00:00.000Z",
        data: { content: "本地旧正文" },
    } as Asset;
    const remoteFresh = { ...structuredClone(fresh), updatedAt: "2026-10-05T00:00:00.000Z", data: { content: "远端新正文" } } as Asset;
    remote.set(remoteFresh.id, remoteFresh);
    useAssetStore.setState({ assets: [ghost, fresh] });
    useCanvasStore.setState({ projects: [canvas([
        { ...textNode(ghost.data.content, "ghost-node"), metadata: { content: ghost.data.content, assetId: ghost.id } },
        { ...textNode(fresh.data.content, "fresh-node"), metadata: { content: fresh.data.content, assetId: fresh.id } },
    ])] });

    await initializeRemoteUserDataSession(scope);
    await repairUnsyncedCanvasTextAssets();

    const puts = requests.filter((request) => request.method === "put");
    expect(puts.map((request) => request.url)).toEqual(["/assets/ghost-text"]);
    expect(remote.get("ghost-text")?.data).toEqual({ content: ghost.data.content });
    expect(remote.get("fresh-text")?.data).toEqual({ content: "远端新正文" });
});

test("同一节点改正文后保存会生成新素材，不覆盖旧文本", async () => {
    await initializeRemoteUserDataSession(scope);
    const first = await ensureCanvasNodeAsset({ canvasId: "canvas-1", node: textNode("第一版文案"), source: "canvas-manual" });
    useAssetStore.getState().updateAsset(first.assetId, { metadata: { nodeId: "text-node", canvasId: "canvas-1", source: "canvas-manual" } });

    const second = await ensureCanvasNodeAsset({ canvasId: "canvas-1", node: textNode("第二版文案"), source: "canvas-manual" });

    expect(second.assetId).not.toBe(first.assetId);
    expect(remote.size).toBe(2);
    expect([...remote.values()].map((asset) => (asset as { data: { content: string } }).data.content).sort()).toEqual(["第一版文案", "第二版文案"]);
});
