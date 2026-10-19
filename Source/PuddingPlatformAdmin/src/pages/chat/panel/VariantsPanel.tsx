import { Button } from 'antd';
import DOMPurify from 'dompurify';
import React, { useMemo, useState } from 'react';
import { AnnotationLayer } from './AnnotationLayer';
import { SandboxedHtml } from './SandboxedHtml';
import type { Annotation, VariantCell, VariantsSpec } from './spec';
import type { UiSubmissionPayload } from './submission';

/**
 * 多方案对比面板（`kind: 'variants'`）。
 *
 * 这是"信息密度"提升最明显的一块：一次看三版，点选 + 标注，
 * 比来回三轮文字描述省事得多。
 *
 * ## 两种内容的渲染路径完全不同（安全决定）
 *
 * - `svg`：由我方消毒后内联（无脚本、无外链，DOMPurify svg profile）；
 * - `html`：**必须**进沙箱 iframe（`sandbox=""`），不允许内联。
 *
 * 不能用同一条路径图省事——HTML 是任意标记，SVG 是受限图形。
 *
 * ## 选中语义
 *
 * `selectMode`：single（默认）/ multi / none。
 * 提交只在点按钮时发生，选择过程不产生任何消息。
 */
export interface VariantsPanelProps {
  spec: VariantsSpec;
  onSubmit: (payload: UiSubmissionPayload) => void;
  disabled?: boolean;
  /** 已有标注（来自 Agent 或用户上次操作），按 targetId 分发到卡片。 */
  annotations?: Annotation[];
}

const sanitizeSvg = (svg: string): string =>
  DOMPurify.sanitize(svg, { USE_PROFILES: { svg: true, svgFilters: true } });

export const VariantsPanel: React.FC<VariantsPanelProps> = ({
  spec,
  onSubmit,
  disabled,
  annotations = [],
}) => {
  const mode = spec.selectMode ?? 'single';
  const [selected, setSelected] = useState<string[]>([]);

  const sanitized = useMemo(() => {
    const map: Record<string, string> = {};
    for (const variant of spec.variants) {
      if (variant.svg) map[variant.id] = sanitizeSvg(variant.svg);
    }
    return map;
  }, [spec.variants]);

  const toggle = (id: string) => {
    if (disabled || mode === 'none') return;
    setSelected((previous) => {
      if (mode === 'multi') {
        return previous.includes(id)
          ? previous.filter((item) => item !== id)
          : [...previous, id];
      }
      return previous[0] === id ? [] : [id];
    });
  };

  const canSubmit = mode === 'none' || selected.length > 0;

  return (
    <div data-testid="panel-variants">
      {spec.title ? <h4 style={{ margin: '0 0 4px' }}>{spec.title}</h4> : null}
      {spec.description ? (
        <p style={{ margin: '0 0 12px', fontSize: 12, opacity: 0.7 }}>{spec.description}</p>
      ) : null}

      <div
        data-testid="variants-grid"
        style={{
          display: 'grid',
          gridTemplateColumns: `repeat(${spec.layout}, minmax(0, 1fr))`,
          gap: 10,
        }}
      >
        {spec.variants.map((variant) => (
          <VariantCard
            key={variant.id}
            variant={variant}
            svgHtml={sanitized[variant.id]}
            selected={selected.includes(variant.id)}
            selectable={mode !== 'none'}
            annotations={annotations}
            onSelect={() => toggle(variant.id)}
          />
        ))}
      </div>

      <div style={{ marginTop: 12, display: 'flex', alignItems: 'center', gap: 12 }}>
        <Button
          type="primary"
          data-testid="panel-variants-submit"
          disabled={disabled || !canSubmit}
          onClick={() =>
            onSubmit({ panelId: spec.id, kind: 'variants', selected, annotations })
          }
        >
          提交选择
        </Button>
        <span style={{ fontSize: 12, opacity: 0.7 }}>
          {selected.length > 0 ? `已选 ${selected.join(' / ')}` : '尚未选择'}
        </span>
      </div>
    </div>
  );
};

interface VariantCardProps {
  variant: VariantCell;
  svgHtml?: string;
  selected: boolean;
  selectable: boolean;
  annotations: Annotation[];
  onSelect: () => void;
}

const VariantCard: React.FC<VariantCardProps> = ({
  variant,
  svgHtml,
  selected,
  selectable,
  annotations,
  onSelect,
}) => (
  <div
    data-testid={`variant-${variant.id}`}
    data-selected={selected ? 'true' : 'false'}
    role={selectable ? 'button' : undefined}
    tabIndex={selectable ? 0 : undefined}
    onClick={onSelect}
    onKeyDown={(event) => {
      if (selectable && (event.key === 'Enter' || event.key === ' ')) {
        event.preventDefault();
        onSelect();
      }
    }}
    style={{
      position: 'relative',
      border: `2px solid ${selected ? '#3370ff' : '#e5e7eb'}`,
      boxShadow: selected ? '0 0 0 3px #e8f0ff' : 'none',
      borderRadius: 10,
      overflow: 'hidden',
      background: '#fff',
      cursor: selectable ? 'pointer' : 'default',
    }}
  >
    <div
      style={{
        padding: '6px 8px',
        fontSize: 12,
        borderBottom: '1px solid #e5e7eb',
        background: '#fafbfc',
      }}
    >
      {variant.id} · {variant.title}
    </div>

    <div style={{ height: 260, overflow: 'hidden', position: 'relative' }}>
      {svgHtml ? (
        <div
          data-testid={`variant-svg-${variant.id}`}
          // 内容已过 DOMPurify（USE_PROFILES.svg）：只允许 SVG 图形，不含脚本。
          dangerouslySetInnerHTML={{ __html: svgHtml }}
          style={{ width: '100%', height: '100%' }}
        />
      ) : (
        // HTML 草稿走沙箱：不允许内联进宿主 DOM。
        <SandboxedHtml
          html={variant.html ?? ''}
          title={`方案 ${variant.id} ${variant.title}`}
          height={260}
        />
      )}

      <AnnotationLayer annotations={annotations} targetId={variant.id} />
    </div>

    {variant.note ? (
      <div style={{ padding: '6px 8px', fontSize: 12, opacity: 0.7 }}>{variant.note}</div>
    ) : null}
  </div>
);

export default VariantsPanel;
