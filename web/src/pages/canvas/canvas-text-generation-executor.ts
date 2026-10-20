import { nanoid } from "nanoid";

import { NODE_DEFAULT_SIZE } from "@/constant/canvas";
import { getGenerationCount, isGenerationCanceled, runCanvasGenerationTaskToConsumer } from "@/lib/canvas/canvas-project-generation";
import { canvasGenerationPromptMetadata } from "@/lib/canvas/canvas-generation-submission";
import { pushCurrentMediaToHistory } from "@/lib/canvas/canvas-media-history";
import { canGenerateTextInPlace, rollbackTextInPlaceGeneration } from "@/lib/canvas/canvas-text-generation";
import { generationFailureMetadata } from "@/lib/generation-error";
import { CanvasNodeType, type CanvasNodeData } from "@/types/canvas";

import type { CanvasGenerationExecution } from "./canvas-generation-executor-types";

const NODE_STATUS_LOADING = "loading" as const;
const NODE_STATUS_SUCCESS = "success" as const;

export { canGenerateTextInPlace };

export async function executeTextGeneration({
    nodeId,
    sourceNode,
    prompt,
    effectivePrompt,
    generationConfig,
    generationContext,
    controller,
    editingTextNode,
    projectId,
    setNodes,
    setConnections,
    startGenerationRequest,
    finishGenerationRequest,
    bindGenerationTask,
    applyGenerationTaskResult,
    registerPendingNodeIds,
    taskContext,
    skillMetadata,
    retryContext,
}: CanvasGenerationExecution) {
    const isConfigNode = sourceNode?.type === CanvasNodeType.Config;
    const isDirectTextTarget = sourceNode?.type === CanvasNodeType.Text && !sourceNode.metadata?.content?.trim() && !editingTextNode;
    // 独立文本份数（textCount），默认 1，不再复用餐图片数量 count（对齐上游 v0.16 语义）
    const textCount = getGenerationCount(String(sourceNode?.metadata?.textCount ?? 1));
    const parentConfig = NODE_DEFAULT_SIZE[isConfigNode ? CanvasNodeType.Config : CanvasNodeType.Text];
    const textConfig = NODE_DEFAULT_SIZE[CanvasNodeType.Text];
    const parentPosition = sourceNode?.position || { x: 0, y: 0 };
    const generateInPlace = canGenerateTextInPlace(sourceNode);
    const childIds = Array.from({ length: generateInPlace ? Math.max(0, textCount - 1) : textCount }, () => nanoid());
    registerPendingNodeIds(childIds);
    // 原地生成时旧正文入栈用的历史条目 id：失败回滚要靠它把旧正文取回顶层。
    const inPlaceHistoryId = `hist-${nanoid(10)}`;
    if (generateInPlace) {
        // 原地生成：结果写回来源节点本身。旧正文先入历史（顶层 content 随 push 被清掉），
        // 新正文由任务结果写回，这样同一个节点反复生成可以切回历史版本。
        // 待填的空文本节点没有 content，push 是幂等空操作。
        setNodes((current) =>
            current.map((node) =>
                node.id === nodeId
                    ? {
                          ...node,
                          metadata: {
                              ...pushCurrentMediaToHistory(node.metadata, { id: inPlaceHistoryId }),
                              ...canvasGenerationPromptMetadata(prompt, effectivePrompt),
                              status: NODE_STATUS_LOADING,
                              errorDetails: undefined,
                              generationErrorCode: undefined,
                              resourceReloadAvailable: undefined,
                              failedPromptFingerprint: undefined,
                          },
                      }
                    : node,
            ),
        );
    }
    if (childIds.length) {
        // 新拉的结果节点：语义与改动前完全一致 —— 每次生成都新建并连线。
        // 图片/视频有「同一来源归并到同一节点」，文本刻意不做：只有原地生成才有历史可切。
        const childNodes: CanvasNodeData[] = childIds.map((id, index) => ({
            id,
            type: CanvasNodeType.Text,
            title: effectivePrompt.slice(0, 32) || "Generated Text",
            position: {
                x: parentPosition.x + parentConfig.width + 96,
                y: parentPosition.y + parentConfig.height / 2 - textConfig.height / 2 + (index - (childIds.length - 1) / 2) * (textConfig.height + 36),
            },
            width: textConfig.width,
            height: textConfig.height,
            metadata: { ...canvasGenerationPromptMetadata(prompt, effectivePrompt), status: NODE_STATUS_LOADING, fontSize: 14, ...skillMetadata },
        }));
        setNodes((current) => [...current.map((node) => (node.id === nodeId && isConfigNode ? { ...node, metadata: { ...node.metadata, ...canvasGenerationPromptMetadata(prompt, effectivePrompt), status: NODE_STATUS_LOADING, errorDetails: undefined } } : node)), ...childNodes]);
        setConnections((current) => [...current, ...childIds.map((childId) => ({ id: nanoid(), fromNodeId: nodeId, toNodeId: childId }))]);
    }

    const targetIds = generateInPlace ? [nodeId, ...childIds] : childIds;

    targetIds.forEach((targetNodeId) => startGenerationRequest(targetNodeId, nodeId, nodeId, controller));
    try {
        await Promise.all(
            targetIds.map((targetNodeId) =>
                runCanvasGenerationTaskToConsumer(
                    {
                        projectId,
                        nodeId: targetNodeId,
                        ...retryContext,
                        mode: "text",
                        prompt: effectivePrompt,
                        config: generationConfig,
                        referenceImages: generationContext.referenceImages,
                        referenceVideos: generationContext.referenceVideos,
                        signal: controller.signal,
                        metadata: { sourceNodeId: nodeId, ...taskContext, resolvedCharacterVersions: generationContext.resolvedCharacterVersions, ...skillMetadata },
                    },
                    {
                        bindTask: (task) => bindGenerationTask(targetNodeId, task),
                        consumeTask: (task) => applyGenerationTaskResult(targetNodeId, task),
                    },
                )
                    .then(() => ({ nodeId: targetNodeId }))
                    .finally(() => finishGenerationRequest(targetNodeId, controller)),
            ),
        );
    } catch (error) {
        // 原地生成失败：旧正文已经被压进历史、顶层 content 已清空。
        // 外层的失败处理对「来源节点本来就有正文」的场景不改状态（那是给「新拉节点」用的），
        // 所以这里必须自己把旧正文取回顶层，否则节点会变成没有正文的空壳并永远卡在 loading。
        if (generateInPlace) {
            const canceled = isGenerationCanceled(error);
            setNodes((current) =>
                current.map((node) =>
                    node.id === nodeId
                        ? {
                              ...node,
                              metadata: {
                                  ...rollbackTextInPlaceGeneration(node.metadata, inPlaceHistoryId, canceled ? "idle" : "error", sourceNode?.metadata?.richText),
                                  ...(canceled ? {} : generationFailureMetadata(error, prompt)),
                              },
                          }
                        : node,
                ),
            );
        }
        throw error;
    }
    if (controller.signal.aborted) return;
    if (isConfigNode) {
        setNodes((current) => current.map((node) => (node.id === nodeId ? { ...node, metadata: { ...node.metadata, status: NODE_STATUS_SUCCESS, errorDetails: undefined, generationErrorCode: undefined, resourceReloadAvailable: undefined, failedPromptFingerprint: undefined } } : node)));
    }
}
