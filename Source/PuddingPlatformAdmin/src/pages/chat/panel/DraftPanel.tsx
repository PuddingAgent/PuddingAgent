import { Button } from 'antd';
import DOMPurify from 'dompurify';
import React, { useMemo } from 'react';
import { SandboxedHtml } from './SandboxedHtml';
import type { DraftSpec } from './spec';
import type { UiSubmissionPayload } from './submission';

/**
 * 草稿面板（`kind: 'draft'`）：展示一整块 HTML / SVG 草稿，可确认后回传。
 *
 * 用于"给我看看这个页面长什么样"这类场景：不是三选一（那走 variants），
 * 而是**聚焦单一草稿**，配一次"确认"动作。
 *
 * 渲染路径与 variants 完全一致，不能因为"只有一个"就放宽：
 * - html → 沙箱 iframe（`sandbox=""`）
 * - svg  → DOMPurify(USE_PROFILES.svg) 后内联
 */
export interface DraftPanelProps {
  spec: DraftSpec;
  onSubmit: (payload: UiSubmissionPayload) => void;
  disabled?: boolean;
  height?: number;
}

export const DraftPanel: React.FC<DraftPanelProps> = ({
  spec,
  onSubmit,
  disabled,
  height = 460,
}) => {
  const sanitizedSvg = useMemo(
    () =>
      spec.format === 'svg'
        ? DOMPurify.sanitize(spec.source, {
            USE_PROFILES: { svg: true, svgFilters: true },
          })
        : '',
    [spec.format, spec.source],
  );

  return (
    <div data-testid="panel-draft">
      {spec.title ? <h4 style={{ margin: '0 0 4px' }}>{spec.title}</h4> : null}
      {spec.description ? (
        <p style={{ margin: '0 0 12px', fontSize: 12, opacity: 0.7 }}>{spec.description}</p>
      ) : null}

      <div style={{ border: '1px solid #e5e7eb', borderRadius: 10, overflow: 'hidden' }}>
        {spec.format === 'svg' ? (
          <div
            data-testid="draft-svg"
            // 已过 DOMPurify（USE_PROFILES.svg）：只允许图形，不含脚本。
            dangerouslySetInnerHTML={{ __html: sanitizedSvg }}
            style={{ width: '100%', height, background: '#fff' }}
          />
        ) : (
          <SandboxedHtml html={spec.source} title={spec.title ?? '草稿'} height={height} />
        )}
      </div>

      <div style={{ marginTop: 12 }}>
        <Button
          type="primary"
          data-testid="panel-draft-submit"
          disabled={disabled}
          onClick={() => onSubmit({ panelId: spec.id, kind: 'draft' })}
        >
          确认这版
        </Button>
      </div>
    </div>
  );
};

export default DraftPanel;
