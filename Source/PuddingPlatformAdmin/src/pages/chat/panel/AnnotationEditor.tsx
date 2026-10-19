import React, { useRef, useState } from 'react';
import { normalizePoint, normalizeRect, type Point } from './annotationGeometry';
import type { Annotation } from './spec';

/**
 * 标注编辑层：在内容上拖框 / 点击落点，产出**结构化标注**。
 *
 * ## 为什么不是"自由涂鸦"
 *
 * 模型读不懂像素。这里产出的是 `{type:'rect', x,y,w,h}`（0..1 归一化）
 * 与 `{type:'note', at, text}`，可以直接进对话；截图只作为视觉模型的补充。
 *
 * ## 交互
 *
 * - 在区域内按住拖动 → 画框，松手即提交一条 rect 标注；
 * - 双击 → 落一个 note 占位（文字由调用方弹输入框补，默认先给"这里"）；
 * - Esc → 取消当前拖拽。
 *
 * ## 为什么这段交互不写单测
 *
 * 拖拽依赖真实布局（`getBoundingClientRect`），jsdom 里恒为 0，
 * 测试只能得到"什么都没发生"的假绿。真正需要测的换算规则已经抽到
 * `annotationGeometry.ts` 并单独覆盖（反向拖拽、越界裁剪、误触阈值）。
 * 这里只做事件接线，用真机验证。
 */
export interface AnnotationEditorProps {
  onCommit: (annotation: Annotation) => void;
  disabled?: boolean;
  /** 子元素渲染在标注层下方（可变交互）。 */
  children?: React.ReactNode;
}

interface DragState {
  start: Point;
  current: Point;
}

export const AnnotationEditor: React.FC<AnnotationEditorProps> = ({
  onCommit,
  disabled,
  children,
}) => {
  const hostRef = useRef<HTMLDivElement>(null);
  const [drag, setDrag] = useState<DragState | null>(null);

  const relativePoint = (event: React.PointerEvent): Point | null => {
    const host = hostRef.current;
    if (!host) return null;
    const box = host.getBoundingClientRect();
    return { x: event.clientX - box.left, y: event.clientY - box.top };
  };

  const handlePointerDown = (event: React.PointerEvent) => {
    if (disabled) return;
    const point = relativePoint(event);
    if (!point) return;
    hostRef.current?.setPointerCapture(event.pointerId);
    setDrag({ start: point, current: point });
  };

  const handlePointerMove = (event: React.PointerEvent) => {
    if (!drag) return;
    const point = relativePoint(event);
    if (!point) return;
    setDrag({ start: drag.start, current: point });
  };

  const finish = () => {
    if (!drag) return;
    const host = hostRef.current;
    if (host) {
      const box = host.getBoundingClientRect();
      const rect = normalizeRect(
        { width: box.width, height: box.height },
        drag.start,
        drag.current,
      );
      if (rect) onCommit({ type: 'rect', ...rect });
    }
    setDrag(null);
  };

  const handleDoubleClick = (event: React.MouseEvent) => {
    if (disabled) return;
    const host = hostRef.current;
    if (!host) return;
    const box = host.getBoundingClientRect();
    const point = normalizePoint(
      { width: box.width, height: box.height },
      { x: event.clientX - box.left, y: event.clientY - box.top },
    );
    if (point) onCommit({ type: 'note', at: point, text: '这里' });
  };

  const preview =
    drag &&
    normalizeRectForPreview(
      hostRef.current?.getBoundingClientRect(),
      drag.start,
      drag.current,
    );

  return (
    <div
      ref={hostRef}
      data-testid="annotation-editor"
      onPointerDown={handlePointerDown}
      onPointerMove={handlePointerMove}
      onPointerUp={finish}
      onPointerCancel={() => setDrag(null)}
      onDoubleClick={handleDoubleClick}
      onKeyDown={(event) => {
        if (event.key === 'Escape') setDrag(null);
      }}
      style={{
        position: 'relative',
        cursor: disabled ? 'default' : 'crosshair',
        touchAction: 'none',
        userSelect: 'none',
      }}
    >
      {children}
      {preview ? (
        <div
          data-testid="annotation-preview"
          style={{
            position: 'absolute',
            left: `${preview.x * 100}%`,
            top: `${preview.y * 100}%`,
            width: `${preview.w * 100}%`,
            height: `${preview.h * 100}%`,
            border: '2px dashed #3370ff',
            background: 'rgba(51,112,255,0.08)',
            pointerEvents: 'none',
          }}
        />
      ) : null}
    </div>
  );
};

/** 预览用的宽松换算：拖拽过程中允许小于阈值的框（松手时才判定误触）。 */
function normalizeRectForPreview(
  box: DOMRect | undefined,
  start: Point,
  end: Point,
): { x: number; y: number; w: number; h: number } | null {
  if (!box || box.width <= 0 || box.height <= 0) return null;
  const x = Math.min(start.x, end.x) / box.width;
  const y = Math.min(start.y, end.y) / box.height;
  const w = Math.abs(end.x - start.x) / box.width;
  const h = Math.abs(end.y - start.y) / box.height;
  if (w <= 0 || h <= 0) return null;
  return { x, y, w, h };
}

export default AnnotationEditor;
