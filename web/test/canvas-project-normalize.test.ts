import { expect, test } from "bun:test";
import { normalizeCanvasProject } from "../src/lib/canvas/canvas-project-normalize";

test("normalizes missing canvas collection fields without changing valid arrays", () => {
    const nodes = [{ id: "node-1" }];
    const connections = [{ id: "connection-1" }];
    const project = normalizeCanvasProject({
        id: "canvas-1",
        nodes,
        connections,
        chatSessions: [{ id: "session-1", messages: [{ id: "message-1", references: {} }] }],
    });

    expect(project.nodes).toBe(nodes);
    expect(project.connections).toBe(connections);
    expect(project.directorScenes).toEqual([]);
    expect(project.chatSessions).toHaveLength(1);
    expect(project.chatSessions[0].messages).toHaveLength(1);
    expect(project.chatSessions[0].messages[0].references).toEqual([]);
    expect((project as { timeline?: { clips?: unknown[] } }).timeline).toBeUndefined();
});

test("normalizes missing and malformed collection fields to empty arrays", () => {
    const project = normalizeCanvasProject({
        id: "canvas-2",
        nodes: undefined,
        connections: null,
        chatSessions: [{ id: "session-1", messages: undefined, generationEffectKeys: {} }],
        directorScenes: "legacy-value",
        timeline: { version: 2, clips: undefined },
    });

    expect(project.nodes).toEqual([]);
    expect(project.connections).toEqual([]);
    expect(project.directorScenes).toEqual([]);
    expect(project.chatSessions[0].messages).toEqual([]);
    expect(project.chatSessions[0].generationEffectKeys).toEqual([]);
    expect(project.timeline?.clips).toEqual([]);
});
