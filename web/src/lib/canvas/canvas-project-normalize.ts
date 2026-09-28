import type { CanvasProject } from "@/stores/canvas/use-canvas-store";
import type { CanvasAssistantMessage, CanvasAssistantSession } from "@/types/canvas";

function isRecord(value: unknown): value is Record<string, unknown> {
    return Boolean(value) && typeof value === "object" && !Array.isArray(value);
}

function normalizeMessages(value: unknown): CanvasAssistantMessage[] {
    if (!Array.isArray(value)) return [];
    return value.flatMap((item) => {
        if (!isRecord(item)) return [];
        const references = item.references;
        const generationMessage = {
            ...item,
            ...(references !== undefined && !Array.isArray(references) ? { references: [] } : {}),
        } as CanvasAssistantMessage;
        return [generationMessage];
    });
}

function normalizeSessions(value: unknown): CanvasAssistantSession[] {
    if (!Array.isArray(value)) return [];
    return value.flatMap((item) => {
        if (!isRecord(item)) return [];
        const generationEffectKeys = item.generationEffectKeys;
        return [{
            ...item,
            messages: normalizeMessages(item.messages),
            ...(generationEffectKeys !== undefined && !Array.isArray(generationEffectKeys) ? { generationEffectKeys: [] } : {}),
        } as CanvasAssistantSession];
    });
}

/**
 * Normalize persisted or remote canvas data at the boundary before editor code uses it.
 * Older records can omit newly added collection fields; valid fields are preserved.
 */
export function normalizeCanvasProject(value: unknown): CanvasProject {
    const source = isRecord(value) ? value : {};
    const timeline = source.timeline;
    const normalizedTimeline = isRecord(timeline)
        ? { ...timeline, clips: Array.isArray(timeline.clips) ? timeline.clips : [] }
        : undefined;

    return {
        ...source,
        nodes: Array.isArray(source.nodes) ? source.nodes : [],
        connections: Array.isArray(source.connections) ? source.connections : [],
        chatSessions: normalizeSessions(source.chatSessions),
        directorScenes: Array.isArray(source.directorScenes) ? source.directorScenes : [],
        ...(normalizedTimeline ? { timeline: normalizedTimeline } : {}),
    } as CanvasProject;
}

export function normalizeCanvasProjects(value: unknown): CanvasProject[] {
    if (!Array.isArray(value)) return [];
    return value.map(normalizeCanvasProject);
}
