import { CanvasNodeAnnotationDialog, type CanvasImageAnnotationPayload } from "@/components/canvas/canvas-node-annotation-dialog";
import { CanvasNodeCropDialog, type CanvasImageCropRect } from "@/components/canvas/canvas-node-crop-dialog";
import { CanvasNodeMaskEditDialog, type CanvasImageMaskEditPayload } from "@/components/canvas/canvas-node-mask-edit-dialog";
import { CanvasNodeUpscaleDialog, type CanvasImageUpscaleParams } from "@/components/canvas/canvas-node-upscale-dialog";
import { CanvasNodeImageEditDialog, type CanvasImageEditPayload } from "@/components/canvas/canvas-node-image-edit-dialog";
import { CanvasNodeLayerDecompositionDialog, type CanvasImageLayerDecompositionPayload } from "@/components/canvas/canvas-node-layer-decomposition-dialog";
import { CanvasNodeTextEditDialog, type CanvasImageTextEditPayload } from "@/components/canvas/canvas-node-text-edit-dialog";
import type { CanvasNodeData } from "@/types/canvas";
import type { AiConfig } from "@/stores/use-config-store";
import { resolveImageDialogModel } from "@/lib/model-selection";

type CanvasProjectMediaDialogsProps = {
    cropNode: CanvasNodeData | null;
    annotationNode: CanvasNodeData | null;
    annotationEditNode: CanvasNodeData | null;
    maskEditNode: CanvasNodeData | null;
    imageEditNode: CanvasNodeData | null;
    layerDecompositionNode: CanvasNodeData | null;
    textEditNode: CanvasNodeData | null;
    imageEditPreset?: "remove-background" | null;
    upscaleNode: CanvasNodeData | null;
    onCloseCrop: () => void;
    onCloseAnnotation: () => void;
    onCloseAnnotationEdit: () => void;
    onCloseMaskEdit: () => void;
    onCloseUpscale: () => void;
    onCloseImageEdit: () => void;
    onCloseLayerDecomposition: () => void;
    onCloseTextEdit: () => void;
    onCrop: (node: CanvasNodeData, crop: CanvasImageCropRect) => void;
    onAnnotate: (node: CanvasNodeData, dataUrl: string) => void;
    onAnnotationEdit: (node: CanvasNodeData, payload: CanvasImageAnnotationPayload) => void;
    onMaskEdit: (node: CanvasNodeData, payload: CanvasImageMaskEditPayload) => void;
    onUpscale: (node: CanvasNodeData, params: CanvasImageUpscaleParams) => void;
    onImageOperation: (node: CanvasNodeData, payload: CanvasImageEditPayload) => void;
    onLayerDecomposition: (node: CanvasNodeData, payload: CanvasImageLayerDecompositionPayload) => void;
    onDetectText: () => Promise<import("@/components/canvas/canvas-node-text-edit-dialog").CanvasImageTextLine[]>;
    onTextEdit: (node: CanvasNodeData, payload: CanvasImageTextEditPayload) => void;
    config: AiConfig;
};

export function CanvasProjectMediaDialogs({
    cropNode,
    annotationNode,
    annotationEditNode,
    maskEditNode,
    imageEditNode,
    layerDecompositionNode,
    textEditNode,
    imageEditPreset,
    upscaleNode,
    onCloseCrop,
    onCloseAnnotation,
    onCloseAnnotationEdit,
    onCloseMaskEdit,
    onCloseUpscale,
    onCloseImageEdit,
    onCloseLayerDecomposition,
    onCloseTextEdit,
    onCrop,
    onAnnotate,
    onAnnotationEdit,
    onMaskEdit,
    onUpscale,
    onImageOperation,
    onLayerDecomposition,
    onDetectText,
    onTextEdit,
    config,
}: CanvasProjectMediaDialogsProps) {
    // 工具对话框继承的节点/全局模型可能是文本等非图片能力的旧绑定（如节点创建时
    // 画布默认模型是文本模型），直接透传会被后端准入拒绝；统一解析为图片能力兼容模型。
    const maskEditModel = maskEditNode ? resolveImageDialogModel(config, maskEditNode.metadata?.model || config.model) : "";
    const imageEditModel = imageEditNode ? resolveImageDialogModel(config, imageEditNode.metadata?.model || config.model) : "";
    const layerDecompositionModel = layerDecompositionNode ? resolveImageDialogModel(config, layerDecompositionNode.metadata?.model || config.model) : "";
    return (
        <>
            {cropNode?.metadata?.content ? <CanvasNodeCropDialog dataUrl={cropNode.metadata.content} open onClose={onCloseCrop} onConfirm={(crop) => onCrop(cropNode, crop)} /> : null}
            {annotationNode?.metadata?.content ? <CanvasNodeAnnotationDialog image={{ url: annotationNode.metadata.content, storageKey: annotationNode.metadata.storageKey }} open onClose={onCloseAnnotation} onConfirm={(dataUrl) => { if (typeof dataUrl === "string") onAnnotate(annotationNode, dataUrl); }} /> : null}
            {annotationEditNode?.metadata?.content ? <CanvasNodeAnnotationDialog image={{ url: annotationEditNode.metadata.content, storageKey: annotationEditNode.metadata.storageKey }} editMode open onClose={onCloseAnnotationEdit} onConfirm={(payload) => { if (typeof payload !== "string") onAnnotationEdit(annotationEditNode, payload); }} /> : null}
            {maskEditNode?.metadata?.content ? <CanvasNodeMaskEditDialog dataUrl={maskEditNode.metadata.content} config={{ ...config, model: maskEditModel, imageModel: maskEditModel, size: maskEditNode.metadata.size || config.size, quality: maskEditNode.metadata.quality || config.quality, count: String(maskEditNode.metadata.count || config.count) }} open onClose={onCloseMaskEdit} onConfirm={(payload) => onMaskEdit(maskEditNode, payload)} /> : null}
            {upscaleNode?.metadata?.content ? <CanvasNodeUpscaleDialog dataUrl={upscaleNode.metadata.content} open onClose={onCloseUpscale} onConfirm={(params) => onUpscale(upscaleNode, params)} /> : null}
            {imageEditNode?.metadata?.content ? <CanvasNodeImageEditDialog dataUrl={imageEditNode.metadata.content} preset={imageEditPreset} config={{ ...config, model: imageEditModel, imageModel: imageEditModel, size: imageEditNode.metadata.size || config.size, quality: imageEditNode.metadata.quality || config.quality }} open onClose={onCloseImageEdit} onConfirm={(payload) => onImageOperation(imageEditNode, payload)} /> : null}
            {layerDecompositionNode?.metadata?.content ? <CanvasNodeLayerDecompositionDialog dataUrl={layerDecompositionNode.metadata.content} config={{ ...config, model: layerDecompositionModel, imageModel: layerDecompositionModel, size: layerDecompositionNode.metadata.size || config.size, quality: layerDecompositionNode.metadata.quality || config.quality }} open onClose={onCloseLayerDecomposition} onConfirm={(payload) => onLayerDecomposition(layerDecompositionNode, payload)} /> : null}
            {textEditNode?.metadata?.content ? <CanvasNodeTextEditDialog dataUrl={textEditNode.metadata.content} open onClose={onCloseTextEdit} onDetect={onDetectText} onConfirm={(payload) => onTextEdit(textEditNode, payload)} /> : null}
        </>
    );
}
