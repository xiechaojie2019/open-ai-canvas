import { nanoid } from "nanoid";

import { NODE_DEFAULT_SIZE } from "@/constant/canvas";
import { canGenerateImageInPlace, findAvailableGenerationGroupPosition, imageGenerationChildPosition, imageGenerationGroupSize } from "@/lib/canvas/canvas-generation-layout";
import { cancelIncompleteImageBatch, collapseCompletedImageBatch, hasImageBatchResult, reconcileImageBatchRoot, retireImageBatchChildren } from "@/lib/canvas/canvas-image-batch-retry";
import { buildImageGenerationNodeTitle } from "@/lib/canvas/canvas-generation-title";
import { nodeSizeFromRatio } from "@/lib/canvas/canvas-node-size";
import { canvasImageReferenceLimitError, buildImageGenerationMetadata, getGenerationCount, isGenerationCanceled, resetGenerationTaskMetadata, runCanvasGenerationTaskToConsumer } from "@/lib/canvas/canvas-project-generation";
import { imageGenerationReferenceConnections } from "@/lib/canvas/canvas-resource-references";
import { canvasMediaSourceKey, isMediaAggregationTarget, pushCurrentMediaToHistory } from "@/lib/canvas/canvas-media-history";
import { canvasGenerationPromptMetadata } from "@/lib/canvas/canvas-generation-submission";
import { CONTENT_MODERATION_ERROR_CODE, generationFailureMetadata, type GenerationFailureMetadata } from "@/lib/generation-error";
import { CanvasNodeType, type CanvasNodeData } from "@/types/canvas";
import { useCanvasStore } from "@/stores/canvas/use-canvas-store";

import type { CanvasGenerationExecution } from "./canvas-generation-executor-types";

const NODE_STATUS_LOADING = "loading" as const;
const NODE_STATUS_SUCCESS = "success" as const;
const NODE_STATUS_ERROR = "error" as const;
const NODE_STATUS_IDLE = "idle" as const;

export async function executeImageGeneration({
    nodeId,
    sourceNode,
    canvasNodes,
    canvasConnections,
    prompt,
    effectivePrompt,
    generationConfig,
    generationContext,
    controller,
    projectId,
    setNodes,
    setConnections,
    setSelectedNodeIds,
    setSelectedConnectionId,
    setDialogNodeId,
    startGenerationRequest,
    finishGenerationRequest,
    bindGenerationTask,
    applyGenerationTaskResult,
    styleMetadata,
    skillMetadata,
    taskContext,
    retryContext,
    showError,
    registerPendingNodeIds,
}: CanvasGenerationExecution) {
    const referenceLimitError = canvasImageReferenceLimitError(generationConfig, generationContext.referenceImages);
    if (referenceLimitError) {
        showError(referenceLimitError);
        return;
    }
    const count = getGenerationCount(generationConfig.count);
    const isConfigNode = sourceNode?.type === CanvasNodeType.Config;
    const isImageNode = sourceNode?.type === CanvasNodeType.Image;
    const isExistingImageNode = isImageNode && hasImageBatchResult(sourceNode);
    const reuseSourceNode = canGenerateImageInPlace(sourceNode);
    const retired = reuseSourceNode && sourceNode ? retireImageBatchChildren(sourceNode, canvasNodes, canvasConnections) : { nodes: canvasNodes, connections: canvasConnections, removedIds: [] as string[] };
    const workingNodes = retired.nodes;
    const workingConnections = retired.connections;
    // 已有图片生成新结果并保留旧版本；参考图只来自入边，避免把旧结果误当成自身输入。
    const referenceImages = generationContext.referenceImages;
    // 同一生成来源（来源节点 + 参考图集合）此前已经出过图的节点：本次结果仍然写回它，
    // 旧结果压进 metadata.mediaHistory 供切换查看，而不是再新建节点加连线。
    const sourceKey = canvasMediaSourceKey("image", nodeId, referenceImages.map((image) => image.id));
    const aggregationTarget = reuseSourceNode
        ? undefined
        : workingNodes.find((node) => node.type === CanvasNodeType.Image && !node.metadata?.locked && isMediaAggregationTarget(node.metadata, sourceKey));
    const reuseResultNode = Boolean(aggregationTarget);
    const generationType = referenceImages.length ? ("edit" as const) : ("generation" as const);
    const generationMetadata = buildImageGenerationMetadata(generationType, generationConfig, count, referenceImages);
    const parentConfig = NODE_DEFAULT_SIZE[isConfigNode ? CanvasNodeType.Config : isImageNode ? CanvasNodeType.Image : CanvasNodeType.Text];
    const imageDefaults = NODE_DEFAULT_SIZE[CanvasNodeType.Image];
    // 生成中占位框按设置比例显示，避免 16:9 任务显示成默认 340x240。
    const requestedImageSize = nodeSizeFromRatio(generationConfig.size || "auto", imageDefaults.width, imageDefaults.height);
    const imageConfig = requestedImageSize || imageDefaults;
    // auto 图生图沿用来源节点尺寸；用户明确选择比例时必须以目标比例创建节点。
    const referenceNode = referenceImages.length === 1 ? canvasNodes.find((node) => node.id === referenceImages[0].id && node.type === CanvasNodeType.Image) : undefined;
    const imageSizeSource = requestedImageSize ? undefined : isExistingImageNode ? sourceNode : referenceNode;
    const outputNodeSize = imageSizeSource ? { width: imageSizeSource.width, height: imageSizeSource.height } : imageConfig;
    const parentPosition = sourceNode?.position || { x: 0, y: 0 };
    const parentWidth = sourceNode?.width || parentConfig.width;
    const parentHeight = sourceNode?.height || parentConfig.height;
    const rootId = reuseSourceNode ? nodeId : aggregationTarget?.id ?? nanoid();
    const childIds = count > 1 ? Array.from({ length: count }, () => nanoid()) : [];
    const targetIds = count > 1 ? childIds : [rootId];
    registerPendingNodeIds(reuseSourceNode || reuseResultNode ? childIds : [rootId, ...childIds]);
    const rootWidth = outputNodeSize.width;
    const rootHeight = outputNodeSize.height;
    const preferredPosition = {
        x: parentPosition.x + parentWidth + 96,
        y: parentPosition.y + parentHeight / 2 - rootHeight / 2,
    };
    const rootPosition = reuseSourceNode
        ? parentPosition
        : aggregationTarget?.position ?? findAvailableGenerationGroupPosition(workingNodes, preferredPosition, imageGenerationGroupSize({ width: rootWidth, height: rootHeight }, outputNodeSize, childIds.length));
    // 复用历史结果节点时保留它原有的批次身份：批次子图要留着给「展开对比」用，
    // 不能被这次单图生成顺手清掉。
    const batchMetadata = reuseResultNode
        ? {
              isBatchRoot: aggregationTarget?.metadata?.isBatchRoot,
              batchChildIds: aggregationTarget?.metadata?.batchChildIds,
              batchFailedCount: aggregationTarget?.metadata?.batchFailedCount,
              primaryImageId: aggregationTarget?.metadata?.primaryImageId,
              imageBatchExpanded: aggregationTarget?.metadata?.imageBatchExpanded,
          }
        : {
              isBatchRoot: count > 1,
              batchChildIds: count > 1 ? childIds : undefined,
              batchFailedCount: count > 1 ? 0 : undefined,
              primaryImageId: undefined,
              imageBatchExpanded: count > 1 ? true : undefined,
          };

    const rootNode: CanvasNodeData = {
        id: rootId,
        type: CanvasNodeType.Image,
        title: buildImageGenerationNodeTitle(effectivePrompt, sourceNode),
        position: rootPosition,
        width: rootWidth,
        height: rootHeight,
        metadata: {
            ...resetGenerationTaskMetadata(
                reuseSourceNode
                    ? sourceNode?.metadata
                    : reuseResultNode
                      ? pushCurrentMediaToHistory(aggregationTarget?.metadata, { id: `hist-${nanoid(10)}` })
                      : undefined,
                NODE_STATUS_LOADING,
            ),
            ...canvasGenerationPromptMetadata(prompt, effectivePrompt),
            status: NODE_STATUS_LOADING,
            size: generationConfig.size,
            ...batchMetadata,
            batchUsesReferenceImages: referenceImages.length > 0,
            // 复用结果节点时旧图已经压进 mediaHistory，这里不能再写回 content。
            content: reuseSourceNode ? "" : undefined,
            storageKey: undefined,
            assetId: undefined,
            ...generationMetadata,
            ...styleMetadata,
            ...skillMetadata,
            generationErrorCode: undefined,
            resourceReloadAvailable: undefined,
            failedPromptFingerprint: undefined,
            // 来源标记：同一来源的下一次生成据此找回本节点并原地更新。
            generationSourceKey: sourceKey,
        },
    };
    const childNodes: CanvasNodeData[] = childIds.map((id, index) => ({
        id,
        type: CanvasNodeType.Image,
        title: buildImageGenerationNodeTitle(effectivePrompt, sourceNode, index, count),
        position: imageGenerationChildPosition(rootNode.position, rootNode.width, outputNodeSize, index),
        width: outputNodeSize.width,
        height: outputNodeSize.height,
        metadata: {
            ...canvasGenerationPromptMetadata(prompt, effectivePrompt),
            status: NODE_STATUS_LOADING,
            size: generationConfig.size,
            batchRootId: rootId,
            ...generationMetadata,
            ...styleMetadata,
            ...skillMetadata,
            generationErrorCode: undefined,
            resourceReloadAvailable: undefined,
            failedPromptFingerprint: undefined,
        },
    }));
    const connectsSelf = !isExistingImageNode || referenceImages.some((img) => img.id === nodeId);
    // 复用结果节点时来源连线早已存在，不能重复追加：imageGenerationReferenceConnections 自带去重，
    // 「来源节点自身」那条边要在这里显式判重。
    const selfEdgeExists = workingConnections.some((connection) => connection.fromNodeId === nodeId && connection.toNodeId === rootId);
    const batchConnections = [
        ...(reuseSourceNode ? [] : imageGenerationReferenceConnections(nodeId, rootId, workingNodes, workingConnections, nanoid)),
        ...(reuseSourceNode || !connectsSelf || selfEdgeExists ? [] : [{ id: nanoid(), fromNodeId: nodeId, toNodeId: rootId }]),
        ...childIds.map((childId) => ({ id: nanoid(), fromNodeId: rootId, toNodeId: childId })),
    ];

    const nextNodes: CanvasNodeData[] = [
        ...workingNodes.map((node) => {
            // 复用既有结果节点：保留原位置与尺寸，只换元数据（旧图已经在 mediaHistory 里）。
            if (reuseResultNode && node.id === rootId) {
                return { ...node, title: rootNode.title, metadata: rootNode.metadata };
            }
            if (node.id !== nodeId) return node;
            if (isConfigNode) return { ...node, metadata: { ...node.metadata, ...canvasGenerationPromptMetadata(prompt, effectivePrompt), status: NODE_STATUS_LOADING, errorDetails: undefined } };
            if (reuseSourceNode) return { ...node, position: rootNode.position, width: rootNode.width, height: rootNode.height, title: rootNode.title, metadata: rootNode.metadata };
            if (isImageNode) return node;
            return {
                ...node,
                type: CanvasNodeType.Text,
                title: prompt.slice(0, 32) || "Prompt",
                width: parentConfig.width,
                height: parentConfig.height,
                metadata: { ...node.metadata, content: prompt, richText: undefined, prompt, composerContent: prompt, status: NODE_STATUS_SUCCESS, fontSize: 14, errorDetails: undefined },
            };
        }),
        ...(reuseSourceNode || reuseResultNode ? [] : [rootNode]),
        ...childNodes,
    ];

    setNodes(nextNodes);
    setConnections((current) => {
        const removed = new Set(retired.removedIds);
        const nextConnections = [...current.filter((connection) => !removed.has(connection.fromNodeId) && !removed.has(connection.toNodeId)), ...batchConnections];
        if (projectId) {
            useCanvasStore.getState().updateProject(projectId, { nodes: nextNodes, connections: nextConnections });
        }
        return nextConnections;
    });
    setSelectedNodeIds(new Set([nodeId]));
    setSelectedConnectionId(null);
    setDialogNodeId(nodeId);

    targetIds.forEach((targetId) => startGenerationRequest(targetId, nodeId, nodeId, controller));
    if (count > 1) startGenerationRequest(rootId, nodeId, nodeId, controller);
    let hasSuccess = false;
    let hasFailure = false;
    let failureCount = 0;
    let representativeFailure: GenerationFailureMetadata | undefined;
    await Promise.all(
        targetIds.map(async (targetId) => {
            try {
                await runCanvasGenerationTaskToConsumer(
                    {
                        projectId,
                        nodeId: targetId,
                        ...retryContext,
                        mode: "image",
                        prompt: effectivePrompt,
                        config: { ...generationConfig, count: "1" },
                        referenceImages,
                        signal: controller.signal,
                        metadata: {
                            sourceNodeId: nodeId,
                            ...taskContext,
                            resolvedCharacterVersions: generationContext.resolvedCharacterVersions,
                            promptTemplateOperation: sourceNode?.metadata?.promptTemplateOperation,
                            promptTemplateVariables: sourceNode?.metadata?.promptTemplateVariables,
                            ...styleMetadata,
                            ...skillMetadata,
                        },
                    },
                    {
                        bindTask: (task) => bindGenerationTask(targetId, task),
                        consumeTask: (task) => applyGenerationTaskResult(targetId, task),
                    },
                );
                if (targetId !== rootId) {
                    setNodes((current) => {
                        const child = current.find((node) => node.id === targetId);
                        const root = current.find((node) => node.id === rootId);
                        if (!child || !hasImageBatchResult(child) || !root || root.metadata?.primaryImageId) return current;
                        const center = { x: root.position.x + root.width / 2, y: root.position.y + root.height / 2 };
                        const geometry = root.metadata?.locked
                            ? {}
                            : {
                                  width: child.width,
                                  height: child.height,
                                  position: { x: center.x - child.width / 2, y: center.y - child.height / 2 },
                              };
                        const updated = current.map((node) =>
                            node.id === rootId
                                ? {
                                      ...node,
                                      ...geometry,
                                      metadata: {
                                          ...node.metadata,
                                          content: child.metadata?.content,
                                          storageKey: child.metadata?.storageKey,
                                          mimeType: child.metadata?.mimeType,
                                          bytes: child.metadata?.bytes,
                                          naturalWidth: child.metadata?.naturalWidth,
                                          naturalHeight: child.metadata?.naturalHeight,
                                          assetId: child.metadata?.assetId,
                                          primaryImageId: targetId,
                                          status: NODE_STATUS_SUCCESS,
                                      },
                                  }
                                : node,
                        );
                        if (projectId) useCanvasStore.getState().updateProject(projectId, { nodes: updated });
                        return updated;
                    });
                }
                hasSuccess = true;
                if (isConfigNode) setNodes((current) => current.map((node) => (node.id === nodeId ? { ...node, metadata: { ...node.metadata, status: NODE_STATUS_SUCCESS, errorDetails: undefined } } : node)));
                return true;
            } catch (error) {
                if (isGenerationCanceled(error)) return false;
                const failure = generationFailureMetadata(error, prompt);
                if (!representativeFailure || failure.generationErrorCode === CONTENT_MODERATION_ERROR_CODE) representativeFailure = failure;
                hasFailure = true;
                failureCount += 1;
                setNodes((current) => {
                    const next = current.map((node) => (node.id === targetId ? { ...node, metadata: { ...node.metadata, status: NODE_STATUS_ERROR, ...failure } } : node));
                    if (projectId) useCanvasStore.getState().updateProject(projectId, { nodes: next });
                    return next;
                });
                return false;
            } finally {
                finishGenerationRequest(targetId, controller);
            }
        }),
    );
    if (count > 1) finishGenerationRequest(rootId, controller);
    if (controller.signal.aborted) {
        setNodes((current) => {
            const cancelled = cancelIncompleteImageBatch(rootId, childIds, current, []);
            if (cancelled.removedIds.length) {
                const removed = new Set(cancelled.removedIds);
                setConnections((connections) => connections.filter((connection) => !removed.has(connection.fromNodeId) && !removed.has(connection.toNodeId)));
            }
            return cancelled.nodes.map((node) => (node.id === nodeId && isConfigNode && node.metadata?.status === NODE_STATUS_LOADING ? { ...node, metadata: { ...node.metadata, status: NODE_STATUS_IDLE, errorDetails: undefined } } : node));
        });
        return;
    }
    if (hasFailure) showError(hasSuccess ? "部分图片生成失败" : "全部图片生成失败");
    setNodes((current) => {
        const next = current.map((node) => {
            if (node.id === nodeId && isConfigNode) {
                return {
                    ...node,
                    metadata: {
                        ...node.metadata,
                        status: hasSuccess ? NODE_STATUS_SUCCESS : NODE_STATUS_ERROR,
                        ...(hasSuccess ? { errorDetails: undefined, generationErrorCode: undefined, failedPromptFingerprint: undefined } : representativeFailure || { errorDetails: "全部图片生成失败" }),
                    },
                };
            }
            if (node.id === rootId) {
                if (count > 1) return collapseCompletedImageBatch(reconcileImageBatchRoot(node, current), current);
                return {
                    ...node,
                    metadata: {
                        ...node.metadata,
                        status: hasSuccess ? NODE_STATUS_SUCCESS : NODE_STATUS_ERROR,
                        batchFailedCount: count > 1 ? failureCount : undefined,
                        ...(hasSuccess ? { errorDetails: undefined, generationErrorCode: undefined, failedPromptFingerprint: undefined } : representativeFailure || { errorDetails: "全部图片生成失败" }),
                    },
                };
            }
            return node;
        });
        if (projectId) useCanvasStore.getState().updateProject(projectId, { nodes: next });
        return next;
    });
}
