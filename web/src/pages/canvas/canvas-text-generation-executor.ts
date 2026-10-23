import { nanoid } from "nanoid";

import { NODE_DEFAULT_SIZE } from "@/constant/canvas";
import { getGenerationCount, isGenerationCanceled, runCanvasGenerationTaskToConsumer } from "@/lib/canvas/canvas-project-generation";
import { canvasGenerationPromptMetadata } from "@/lib/canvas/canvas-generation-submission";
import { pushCurrentMediaToHistory } from "@/lib/canvas/canvas-media-history";
import {
    canGenerateTextInPlace,
    cancelIncompleteTextBatch,
    finalizeCompletedTextBatch,
    hasTextBatchResult,
    rollbackTextInPlaceGeneration,
} from "@/lib/canvas/canvas-text-generation";
import { isGenerationTaskCapacityError } from "@/lib/canvas/canvas-generation-batch";
import { useCanvasStore } from "@/stores/canvas/use-canvas-store";
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
    showError,
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
    let failureCount = 0;
    let successCount = 0;
    let capacityError: unknown;
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
                .then(() => {
                    successCount += 1;
                })
                .catch((error: unknown) => {
                    if (isGenerationCanceled(error)) return;
                    if (isGenerationTaskCapacityError(error)) capacityError ??= error;
                    failureCount += 1;
                    // 单份失败只标记它自己：同批其余结果照常回写、照常收口。
                    setNodes((current) => current.map((node) => (node.id === targetNodeId ? { ...node, metadata: { ...node.metadata, status: "error", ...generationFailureMetadata(error, prompt) } } : node)));
                })
                .finally(() => finishGenerationRequest(targetNodeId, controller)),
        ),
    );
    if (controller.signal.aborted) {
        setNodes((current) => {
            const cancelled = cancelIncompleteTextBatch(nodeId, childIds, current);
            let next: CanvasNodeData[] = cancelled.nodes;
            if (generateInPlace) {
                next = next.map((node) => {
                    if (node.id !== nodeId) return node;
                    // 已落地的新正文保留；否则把生成前压进历史的旧正文取回顶层。
                    return hasTextBatchResult(node)
                        ? { ...node, metadata: { ...node.metadata, status: NODE_STATUS_SUCCESS, errorDetails: undefined, generationErrorCode: undefined } }
                        : { ...node, metadata: rollbackTextInPlaceGeneration(node.metadata, inPlaceHistoryId, "idle", sourceNode?.metadata?.richText) };
                });
            } else if (isConfigNode) {
                next = next.map((node) => (node.id === nodeId ? { ...node, metadata: { ...node.metadata, status: "idle", errorDetails: undefined } } : node));
            }
            const removed = new Set(cancelled.removedIds);
            if (removed.size) {
                setConnections((connections) => {
                    const pruned = connections.filter((connection) => !removed.has(connection.fromNodeId) && !removed.has(connection.toNodeId));
                    if (projectId) useCanvasStore.getState().updateProject(projectId, { nodes: next, connections: pruned });
                    return pruned;
                });
            }
            return next;
        });
        return;
    }
    // 队列容量错误交给外层统一回到 idle（会清掉任务绑定）。但若同批已有结果落地，
    // 就不能整体回滚，按「部分失败」收口，否则会把已经生成的正文重新标成未开始。
    if (capacityError && !successCount) throw capacityError;
    if (failureCount) showError(failureCount >= targetIds.length ? "文本生成失败" : "部分文本生成失败");
    setNodes((current) => {
        let next: CanvasNodeData[] = current.map((node) => {
            if (node.id !== nodeId || !isConfigNode) return node;
            const hasSuccess = childIds.some((childId) => hasTextBatchResult(current.find((item) => item.id === childId)));
            return {
                ...node,
                metadata: {
                    ...node.metadata,
                    status: hasSuccess ? NODE_STATUS_SUCCESS : ("error" as const),
                    ...(hasSuccess ? { errorDetails: undefined, generationErrorCode: undefined, resourceReloadAvailable: undefined, failedPromptFingerprint: undefined } : {}),
                },
            };
        });
        // 多份结果收口成一个节点：来源节点（配置节点则是第一个成功子节点）承载最新正文，
        // 其余成功结果进 mediaHistory，成功子节点连同连线一起从文档删除。
        // 失败子节点保留，供「重试失败部分」继续使用。
        const finalized = finalizeCompletedTextBatch(nodeId, childIds, next);
        next = finalized.nodes;
        if (generateInPlace) {
            // 来源节点自己失败、子结果成功时，结果已经被提升到来源节点；
            // 全都没成功才把生成前的旧正文取回，避免留下没有正文的空壳。
            next = next.map((node) => {
                if (node.id !== nodeId) return node;
                if (hasTextBatchResult(node)) {
                    return { ...node, metadata: { ...node.metadata, status: NODE_STATUS_SUCCESS, errorDetails: undefined, generationErrorCode: undefined, resourceReloadAvailable: undefined, failedPromptFingerprint: undefined } };
                }
                return {
                    ...node,
                    metadata: {
                        ...rollbackTextInPlaceGeneration(node.metadata, inPlaceHistoryId, "error", sourceNode?.metadata?.richText),
                        // 回滚只还原正文；这次失败的原因由任务的失败回写挂在 metadata 上，没有则给一句兜底。
                        errorDetails: node.metadata?.errorDetails || "文本生成失败",
                    },
                };
            });
        }
        if (finalized.removedIds.length) {
            const removed = new Set(finalized.removedIds);
            setConnections((connections) => {
                const pruned = connections.filter((connection) => !removed.has(connection.fromNodeId) && !removed.has(connection.toNodeId));
                if (projectId) useCanvasStore.getState().updateProject(projectId, { nodes: next, connections: pruned });
                return pruned;
            });
        } else if (projectId) {
            useCanvasStore.getState().updateProject(projectId, { nodes: next });
        }
        return next;
    });
}
