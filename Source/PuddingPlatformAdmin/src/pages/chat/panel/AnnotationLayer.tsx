import React from 'react';
import type { Annotation } from './spec';

/**
 * 标注图层：把结构化图元画在内容之上。
 *
 * ## 为什么是 SVG + 百分比定位，而不是 canvas
 *
 * 标注要跟着容器缩放走。用 SVG `viewBox="0 0 1 1"` + `preserveAspectRatio="none"`
 * 时，一个单位 = 容器宽高的 100%，坐标直接就是 spec 里的 0..1 归一化值，
 * 不需要任何换算。线宽用 `vector-effect="non-scaling-stroke"` 固定为像素，
 * 否则横向会被拉成一条粗线。
 *
 * ## 为什么 note 用 div 而不是 SVG text
 *
 * 文字在非等比缩放的 SVG 里会被拉伸变形；用绝对定位的 div 才能保持字形正常。
 *
 * ## 只负责"画"，不负责"改"
 *
 * 编辑（拖拽画框、输入文字）是另一层的事，这里只做只读渲染——
 * 这样它可以被任何容器复用（变体卡片、文档、截图）。
 */
export interface AnnotationLayerProps {
  annotations: Annotation[];
  /** 只显示针对某个子元素（variant id 等）的标注；未指定的标注始终显示。 */
  targetId?: string;
}

export const AnnotationLayer: React.FC<AnnotationLayerProps> = ({
  annotations,
  targetId,
}) => {
  const visible = annotations.filter(
    (item) => !item.targetId || !targetId || item.targetId === targetId,
  );
  if (visible.length === 0) return null;

  const shapes = visible.filter((item) => item.type === 'rect' || item.type === 'arrow');
  const notes = visible.filter((item) => item.type === 'note');

  return (
    <div
      data-testid="annotation-layer"
      style={{ position: 'absolute', inset: 0, pointerEvents: 'none' }}
    >
      <svg
        viewBox="0 0 1 1"
        preserveAspectRatio="none"
        style={{ position: 'absolute', inset: 0, width: '100%', height: '100%' }}
      >
        {shapes.map((item, index) => {
          if (item.type === 'rect') {
            return (
              <rect
                key={`r-${index}`}
                x={item.x}
                y={item.y}
                width={item.w}
                height={item.h}
                fill="none"
                stroke="#f53f3f"
                strokeWidth={2}
                vectorEffect="non-scaling-stroke"
              />
            );
          }
          return (
            <line
              key={`a-${index}`}
              x1={item.from.x}
              y1={item.from.y}
              x2={item.to.x}
              y2={item.to.y}
              stroke="#f53f3f"
              strokeWidth={2}
              vectorEffect="non-scaling-stroke"
            />
          );
        })}
      </svg>

      {notes.map((item, index) => (
        <div
          key={`n-${index}`}
          data-testid="annotation-note"
          style={{
            position: 'absolute',
            left: `${item.at.x * 100}%`,
            top: `${item.at.y * 100}%`,
            transform: 'translateY(-100%)',
            background: '#f53f3f',
            color: '#fff',
            fontSize: 11,
            lineHeight: 1.4,
            padding: '2px 6px',
            borderRadius: 4,
            whiteSpace: 'nowrap',
            maxWidth: '80%',
            overflow: 'hidden',
            textOverflow: 'ellipsis',
          }}
        >
          {item.text}
        </div>
      ))}

      {/* rect / arrow 附带的文字：贴在图形左上角，避免和 note 抢位置 */}
      {shapes
        .filter((item) => item.text)
        .map((item, index) => {
          const point = item.type === 'rect' ? { x: item.x, y: item.y } : item.from;
          return (
            <div
              key={`t-${index}`}
              style={{
                position: 'absolute',
                left: `${point.x * 100}%`,
                top: `${point.y * 100}%`,
                transform: 'translateY(-100%)',
                background: '#f53f3f',
                color: '#fff',
                fontSize: 11,
                padding: '1px 5px',
                borderRadius: 4,
                whiteSpace: 'nowrap',
              }}
            >
              {item.text}
            </div>
          );
        })}
    </div>
  );
};

export default AnnotationLayer;
