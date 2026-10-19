import { CanvasNodeType, type CanvasNodeData, type Position } from "@/types/canvas";

type Size = {
    width: number;
    height: number;
};

/** 生成组与来源节点之间的横向偏移。 */
const BATCH_CHILD_OFFSET_X = 120;
/** 同组内相邻子图的间距（多张排布时纵向为主）。 */
const BATCH_CHILD_GAP = 48;
/** 与既有节点保持的安全间距：横向 ≥120px、纵向 ≥48px。 */
const COLLISION_GAP_X = 120;
const COLLISION_GAP_Y = 48;
/** 就近空位的最大搜索圈数（曼哈顿步数），避免极端布局下无限搜索。 */
const MAX_PLACEMENT_STEPS = 12;

export function canGenerateImageInPlace(sourceNode: CanvasNodeData | undefined) {
    return canGenerateMediaInPlace(sourceNode, CanvasNodeType.Image);
}

export function canGenerateMediaInPlace(sourceNode: CanvasNodeData | undefined, mediaType: typeof CanvasNodeType.Image | typeof CanvasNodeType.Video | typeof CanvasNodeType.Audio) {
    if (sourceNode?.type !== mediaType) return false;
    if (sourceNode.metadata?.generationResultPlacement) return sourceNode.metadata.generationResultPlacement === "replace-node";
    if (!sourceNode.metadata?.content && !sourceNode.metadata?.storageKey) return true;
    // 兼容显式落点字段引入前创建的复制节点和版本节点。
    return Boolean(sourceNode.metadata?.copiedFromNodeId || sourceNode.metadata?.versionOfNodeId);
}

/** 生成组排布：1 = 单列纵向（默认），2 = 双列网格（空间不足时降级）。 */
export type GenerationLayoutColumns = 1 | 2;

export type GenerationGroupPlacement = {
    position: Position;
    columns: GenerationLayoutColumns;
};

/**
 * 生成组包围盒。单列纵向优先（规格情况 A），空间不足时才用双列（情况 B）。
 */
export function imageGenerationGroupSize(rootSize: Size, imageSize: Size, childCount: number, columns: GenerationLayoutColumns = 2): Size {
    if (childCount <= 0) return rootSize;
    const perRow = columns;
    const rows = Math.ceil(childCount / perRow);
    return {
        width: rootSize.width + BATCH_CHILD_OFFSET_X + perRow * imageSize.width + (perRow - 1) * BATCH_CHILD_GAP,
        height: Math.max(rootSize.height, rows * imageSize.height + (rows - 1) * BATCH_CHILD_GAP),
    };
}

export function imageGenerationChildPosition(rootPosition: Position, rootWidth: number, imageSize: Size, index: number, columns: GenerationLayoutColumns = 2): Position {
    return {
        x: rootPosition.x + rootWidth + BATCH_CHILD_OFFSET_X + (index % columns) * (imageSize.width + BATCH_CHILD_GAP),
        y: rootPosition.y + Math.floor(index / columns) * (imageSize.height + BATCH_CHILD_GAP),
    };
}

/**
 * 就近空位落点（规格情况 A/B/C）：
 * A. 偏好位置（来源右侧 + 横向偏移、纵向对齐中心）放得下 → 直接用；
 * B. 放不下 → 同一位置降级为双列网格再试；
 * C. 还放不下 → 整组向右逐列平移找最近的空位，一行扫完再整体下移一行。
 * 只移动本次新增节点，既有节点一律不动。
 */
export function resolveGenerationGroupPlacement(
    nodes: CanvasNodeData[],
    preferred: Position,
    rootSize: Size,
    imageSize: Size,
    childCount: number,
): GenerationGroupPlacement {
    const layouts: GenerationLayoutColumns[] = childCount > 1 ? [1, 2] : [1];

    for (const layout of layouts) {
        const size = imageGenerationGroupSize(rootSize, imageSize, childCount, layout);
        if (!overlapsAny(nodes, preferred, size)) return { position: preferred, columns: layout };
    }

    for (let step = 0; step <= MAX_PLACEMENT_STEPS; step += 1) {
        // 按曼哈顿距离逐圈找最近空位：row + column = step。
        // 右侧被占时下移会回到起点正下方（x 不累积），不会一路向右漂走。
        for (let row = 0; row <= step; row += 1) {
            const column = step - row;
            for (const layout of layouts) {
                const size = imageGenerationGroupSize(rootSize, imageSize, childCount, layout);
                const candidate: Position = {
                    x: preferred.x + column * (size.width + COLLISION_GAP_X),
                    y: preferred.y + row * (size.height + COLLISION_GAP_Y),
                };
                if (!overlapsAny(nodes, candidate, size)) return { position: candidate, columns: layout };
            }
        }
    }

    // 极端拥挤：行列都占满时退回偏好位置，交由上层照常渲染（不静默丢弃生成结果）。
    return { position: preferred, columns: layouts[0] };
}

function overlapsAny(nodes: CanvasNodeData[], candidate: Position, size: Size) {
    return nodes.some((node) => rectanglesOverlap(candidate, size, node.position, node));
}

function rectanglesOverlap(firstPosition: Position, firstSize: Size, secondPosition: Position, secondSize: Size) {
    return !(
        firstPosition.x + firstSize.width + COLLISION_GAP_X <= secondPosition.x ||
        firstPosition.x >= secondPosition.x + secondSize.width + COLLISION_GAP_X ||
        firstPosition.y + firstSize.height + COLLISION_GAP_Y <= secondPosition.y ||
        firstPosition.y >= secondPosition.y + secondSize.height + COLLISION_GAP_Y
    );
}