import { isValidElement, type ComponentProps, type ReactNode } from "react";
import { Focusable, Tooltip as RACTooltip, TooltipTrigger, type Placement } from "react-aria-components";

import { cn } from "@/lib/utils";

/**
 * Tooltip — 气泡提示（自研，react-aria-components overlay）。
 *
 * - API 对齐 AntD Tooltip（title/placement/children），title 为空时不渲染浮层。
 * - 浮层定位、进出场、焦点管理由 RAC 承担（ADR-0008：复杂浮层以 RAC 为无障碍地基）。
 * - 样式只吃语义 token：bg-surface-strong（light #fff / dark #2a2a2a）+ text-foreground +
 *   border-border hairline，无裸色；motion 走 RAC 动画类 + motion-reduce 关停。
 * - 启用的控件直接接收 RAC 事件、ref 和描述；非交互/禁用内容用 span 接收悬停。
 * - placement 映射 AntD → RAC（topLeft→top left …）。
 * 对标：AntD Tooltip；boardui base/tooltip。
 */

export type TooltipPlacement = "top" | "topLeft" | "topRight" | "bottom" | "bottomLeft" | "bottomRight" | "left" | "right";

/** ref 必然落在 DOM 元素上的原生可交互标签，可安全走 direct 分支。 */
const HOST_INTERACTIVE_TAGS = ["button", "a", "input", "select", "textarea", "summary"];

const PLACEMENT_MAP: Record<TooltipPlacement, Placement> = {
    top: "top",
    topLeft: "top left",
    topRight: "top right",
    bottom: "bottom",
    bottomLeft: "bottom left",
    bottomRight: "bottom right",
    left: "left",
    right: "right",
};

export interface TooltipProps {
    title?: ReactNode;
    placement?: TooltipPlacement;
    /** 悬停延迟 ms（默认 350，贴近 AntD mouseEnterDelay） */
    delay?: number;
    className?: string;
    children: ReactNode;
}

export function Tooltip({ title, placement = "top", delay = 350, className, children }: TooltipProps) {
    if (!title) return <>{children}</>;

    const child = isValidElement<{ disabled?: boolean; loading?: boolean; tabIndex?: number }>(children) ? children : null;
    // direct 分支会让 Focusable 把 RAC 的 triggerRef 原样透传给 child——Focusable 自身不渲染任何
    // DOM，ref 落不落在元素上完全取决于 child。antd v6 起部分复合组件（如 InputNumber）把 ref 指向
    // proxyObject 而非 DOM 元素，RAC 定位浮层时 ResizeObserver.observe(proxy) 会抛
    // "parameter 1 is not of type 'Element'" 并触发错误边界整页崩溃。
    // 因此只有 ref 必然是 DOM 元素的原生标签才走 direct 分支，其余组件一律经外壳 span 承接悬停。
    const directTrigger =
        child && typeof child.type === "string" && !child.props.disabled && !child.props.loading && (HOST_INTERACTIVE_TAGS.includes(child.type) || child.props.tabIndex !== undefined);

    return (
        <TooltipTrigger delay={delay}>
            {/*
             * RAC 1.21 的 TooltipTrigger 通过 context 下发 hover/focus 处理与 triggerRef，
             * 只有消费该上下文的组件才会真正挂上（原生 <span>/<button> 不会读）。直接套
             * 普通 span 的写法因此既没有触发事件、也没有定位基准，鼠标悬停永远不弹。
             * useFocus 只处理 target === currentTarget，不能依赖内层按钮焦点冒泡到 span。
             * 直接把事件和 aria-describedby 交给启用的控件，不增加额外 Tab 停靠点。
             * 自定义控件需透传 DOM props/ref；禁用或非交互内容仍由外壳接收悬停。
             */}
            <span className="inline-flex">
                {directTrigger ? (
                    <Focusable>{child as ComponentProps<typeof Focusable>["children"]}</Focusable>
                ) : (
                    <Focusable excludeFromTabOrder>
                        <span className="inline-flex">{children}</span>
                    </Focusable>
                )}
            </span>
            <RACTooltip placement={PLACEMENT_MAP[placement]} offset={6} className={cn("z-50 max-w-64 rounded-md border border-border bg-surface-strong px-2 py-1 text-xs leading-relaxed text-foreground shadow-md", "ra-pop-in", className)}>
                {title}
            </RACTooltip>
        </TooltipTrigger>
    );
}
