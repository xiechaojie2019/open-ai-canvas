import { canonicalize } from "json-canonicalize";

import type { NodeGenerationContext } from "@/components/canvas/canvas-node-generation";
import type { CanvasNodeGenerationMode } from "@/components/canvas/canvas-node-prompt-panel";
import { generationPromptFingerprint } from "@/lib/generation-error";

type GenerationReference = NodeGenerationContext["referenceImages"][number] | NodeGenerationContext["referenceVideos"][number] | NodeGenerationContext["referenceAudios"][number];

export type CanvasGenerationRequestFingerprintInput = {
    nodeId: string;
    mode: CanvasNodeGenerationMode;
    prompt: string;
    model: string;
    options: Record<string, unknown>;
    workflow?: {
        provider?: string;
        workflowId?: string;
        workflowKind?: string;
        parameters?: Record<string, unknown>;
    };
    operation?: string;
    audioInstructions?: string;
    promptTemplateOperation?: string;
    promptTemplateVariables?: Record<string, string>;
    context: Pick<NodeGenerationContext, "referenceImages" | "referenceVideos" | "referenceAudios" | "characterReferences" | "resolvedCharacterVersions" | "resolvedCharacterVoices">;
};

/**
 * 编辑器保留带 @ 槽位的原始提示词，任务请求单独保存经过技能/风格处理的模型提示词。
 * 模型提示词也保留 @图片1 等槽位标记。
 * 两者不能互相覆盖，否则刷新后富引用会退化为普通“图片1”文本。
 */
export function canvasGenerationPromptMetadata(composerContent: string, effectivePrompt: string) {
    return { composerContent, prompt: effectivePrompt };
}

/**
 * 一次提交出多张（节点「生成数量」> 1）时，给第 `index` 张拼上它自己的位次说明。
 *
 * 上游一次调用只返回一张图，所以多张是「同一段配置发 N 次」。以前这 N 次用的是**逐字相同**
 * 的提示词，于是「生成三张不同花色的小猫不要拼图」这种描述会被模型画进同一张图里
 * （拼图 / 多主体并排），N 张结果几乎一模一样，等于白花 N 倍算力。
 * 这里补上位次 + 「本次只出一张」的硬约束，让每张都是独立完整的单幅画面。
 *
 * `count <= 1` 时原样返回，单张路径行为不变。
 */
export function imageBatchItemPrompt(prompt: string, index: number, count: number) {
    if (count <= 1) return prompt;
    return `${prompt.trim()}\n\n【本次只生成这一张：第 ${index + 1} 张（共 ${count} 张）。只输出一幅独立完整的单张画面，不要拼图、不要分格、不要多个主体并排。】`;
}

export function canvasGenerationRequestFingerprint(input: CanvasGenerationRequestFingerprintInput) {
    const serialized = canonicalize({
        version: 1,
        nodeId: input.nodeId,
        mode: input.mode,
        prompt: input.prompt.trim(),
        model: input.model,
        options: input.options,
        workflow: input.workflow,
        operation: input.operation,
        audioInstructions: input.audioInstructions,
        promptTemplateOperation: input.promptTemplateOperation,
        promptTemplateVariables: input.promptTemplateVariables,
        references: {
            images: input.context.referenceImages.map(referenceIdentity),
            videos: input.context.referenceVideos.map(referenceIdentity),
            audios: input.context.referenceAudios.map(referenceIdentity),
            characters: input.context.characterReferences,
            resolvedCharacterVersions: input.context.resolvedCharacterVersions,
            resolvedCharacterVoices: input.context.resolvedCharacterVoices,
        },
    });
    return `canvas-generation:v1:${generationPromptFingerprint(serialized)}`;
}

function referenceIdentity(reference: GenerationReference) {
    const source = "storageKey" in reference && reference.storageKey ? reference.storageKey : "url" in reference && reference.url ? reference.url : "dataUrl" in reference ? reference.dataUrl : "";
    return {
        id: reference.id,
        name: reference.name,
        type: reference.type,
        source: source.startsWith("data:") ? `data:${generationPromptFingerprint(source)}` : source,
        derivedSource: "source" in reference ? reference.source : undefined,
        durationMs: "durationMs" in reference ? reference.durationMs : undefined,
    };
}

export function runCanvasGenerationSubmissionOnce<T>(locks: Map<string, Promise<unknown>>, nodeId: string, operation: () => Promise<T>, onDuplicate?: () => void): Promise<T> {
    const existing = locks.get(nodeId) as Promise<T> | undefined;
    if (existing) {
        onDuplicate?.();
        return existing;
    }
    const running = Promise.resolve()
        .then(operation)
        .finally(() => {
            if (locks.get(nodeId) === running) locks.delete(nodeId);
        });
    locks.set(nodeId, running);
    return running;
}
