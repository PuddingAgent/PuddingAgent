import React from 'react';
import {
  SIDE_PANEL_DEFAULT_WIDTH_PX,
  SIDE_PANEL_MAX_WIDTH_PX,
  SIDE_PANEL_MIN_WIDTH_PX,
  clampWidth,
  widthAfterDrag,
} from '../sideArtifact/sidePanelSizing';

/**
 * 聊天列与右侧产物区之间的分隔条。
 *
 * ## 为什么需要它
 *
 * 用户实测反馈："我还无法拖动这个文档区域的尺寸"。
 * 固定宽度只能满足一种场景：窄了读不下长文档，宽了聊天列被压。
 * 唯一可行的办法是把决定权交给用户，并记住他的选择。
 *
 * ## 交互
 *
 * - 拖拽：面板在右侧，向左拖 = 变宽（`widthAfterDrag` 里 `start - delta`）
 * - 键盘：← 变宽 / → 变窄，每次 16px（无障碍）
 * - 双击：复位到默认宽度
 *
 * ## 为什么拖拽时不写 localStorage
 *
 * pointermove 每帧都触发，逐帧写 storage 会明显掉帧。
 * 所以拖动过程中只更新内存宽度，`onCommit` 在 pointerup 才落盘。
 */
export interface SidePanelResizerProps {
  width: number;
  /** 拖动过程中持续回调（只更新内存）。 */
  onWidthChange: (width: number) => void;
  /** 拖动结束 / 键盘调整后回调（用于持久化）。 */
  onCommit?: (width: number) => void;
}

const KEYBOARD_STEP_PX = 16;

export const SidePanelResizer: React.FC<SidePanelResizerProps> = ({
  width,
  onWidthChange,
  onCommit,
}) => {
  const dragging = React.useRef<{ startX: number; startWidth: number } | null>(null);

  // 最新宽度：拖动过程中父组件的 state 在变，但 window 上注册的监听是
  // **按下那一刻**的闭包。不读 ref 的话，松手时会提交拖动**开始**的宽度，
  // 拖动结果被丢掉，下次打开又变回原宽度。
  const widthRef = React.useRef(width);
  widthRef.current = width;

  // 回调也锁在 ref 里。调用方传内联箭头函数（很常见）时，`onCommit` 每次渲染
  // 都是新引用 → `stopDragging` 跟着变 → 下面那个 cleanup effect 每帧跑一次，
  // 把刚注册的 pointermove/pointerup 监听摘掉 —— 实测表现是"拖一下就停"。
  const onWidthChangeRef = React.useRef(onWidthChange);
  onWidthChangeRef.current = onWidthChange;
  const onCommitRef = React.useRef(onCommit);
  onCommitRef.current = onCommit;

  const handlePointerMove = React.useCallback((e: PointerEvent) => {
    const state = dragging.current;
    if (!state) return;
    onWidthChangeRef.current(widthAfterDrag(state.startWidth, e.clientX - state.startX));
  }, []);

  /** 拖拽期间禁止选中文本 / 保持列宽光标；结束后必须恢复，否则整页都选不中字。 */
  const setDragChrome = (on: boolean) => {
    if (typeof document === 'undefined') return;
    document.body.style.userSelect = on ? 'none' : '';
    document.body.style.cursor = on ? 'col-resize' : '';
  };

  const stopDragging = React.useCallback(() => {
    const state = dragging.current;
    dragging.current = null;
    window.removeEventListener('pointermove', handlePointerMove);
    window.removeEventListener('pointerup', stopDragging);
    setDragChrome(false);
    if (state) onCommitRef.current?.(widthRef.current);
  }, [handlePointerMove]);

  // 组件卸载时摘掉监听，避免拖拽中途被卸载后仍然改 state。
  React.useEffect(
    () => () => {
      window.removeEventListener('pointermove', handlePointerMove);
      window.removeEventListener('pointerup', stopDragging);
      setDragChrome(false);
    },
    [handlePointerMove, stopDragging],
  );

  const handlePointerDown = (e: React.PointerEvent<HTMLDivElement>) => {
    dragging.current = { startX: e.clientX, startWidth: width };
    // 指针捕获：文档面板里可能有 iframe（沙箱 HTML 草稿），拖到它上面时
    // 事件会被嵌套文档吞掉，表现为"拖到一半就不动了"。捕捉后事件仍回到本元素。
    try {
      e.currentTarget.setPointerCapture?.(e.pointerId);
    } catch {
      /* jsdom / 旧浏览器无此 API，忽略 */
    }
    setDragChrome(true);
    window.addEventListener('pointermove', handlePointerMove);
    window.addEventListener('pointerup', stopDragging);
  };

  const handleKeyDown = (e: React.KeyboardEvent<HTMLDivElement>) => {
    if (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight') return;
    e.preventDefault();
    // 面板在右侧：左箭头 = 面板变宽。
    const next = clampWidth(width + (e.key === 'ArrowLeft' ? KEYBOARD_STEP_PX : -KEYBOARD_STEP_PX));
    onWidthChange(next);
    onCommit?.(next);
  };

  const reset = () => {
    onWidthChange(SIDE_PANEL_DEFAULT_WIDTH_PX);
    onCommit?.(SIDE_PANEL_DEFAULT_WIDTH_PX);
  };

  return (
    <div
      role="separator"
      aria-orientation="vertical"
      aria-label="调整文档面板宽度"
      aria-valuemin={SIDE_PANEL_MIN_WIDTH_PX}
      aria-valuemax={SIDE_PANEL_MAX_WIDTH_PX}
      aria-valuenow={clampWidth(width)}
      tabIndex={0}
      data-testid="side-panel-resizer"
      onPointerDown={handlePointerDown}
      onKeyDown={handleKeyDown}
      onDoubleClick={reset}
      style={{
        flex: '0 0 auto',
        width: 6,
        cursor: 'col-resize',
        background: 'transparent',
        borderLeft: '1px solid #e5e7eb',
        // 键盘聚焦时给一条可见的焦点线，否则纯鼠标用户看不出这里能拖。
        outlineOffset: -1,
        touchAction: 'none',
      }}
    />
  );
};

export default SidePanelResizer;
