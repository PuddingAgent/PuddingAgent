import React from 'react';
import type { PanelSpec } from './spec';
import type { UiSubmissionPayload } from './submission';
import { SurveyPanel } from './SurveyPanel';
import { VariantsPanel } from './VariantsPanel';
import { DraftPanel } from './DraftPanel';

/**
 * 面板总入口：按 `spec.kind` 分派渲染器。
 *
 * 未实现的类型**显式降级**而不是空白——面板是全屏区域，留白会让用户
 * 以为坏了。降级时把原因写清楚，并保留 spec 供排障。
 */
export interface PanelViewProps {
  spec: PanelSpec;
  onSubmit: (payload: UiSubmissionPayload) => void;
  disabled?: boolean;
}

export const PanelView: React.FC<PanelViewProps> = ({ spec, onSubmit, disabled }) => {
  switch (spec.kind) {
    case 'survey':
      return <SurveyPanel spec={spec} onSubmit={onSubmit} disabled={disabled} />;
    case 'variants':
      return <VariantsPanel spec={spec} onSubmit={onSubmit} disabled={disabled} />;
    case 'draft':
      return <DraftPanel spec={spec} onSubmit={onSubmit} disabled={disabled} />;
    default:
      return (
        <div data-testid="panel-unsupported" role="note">
          未知面板类型。
        </div>
      );
  }
};

export default PanelView;
