// ── P2#9 AutoReviewIndicator：Auto-review classifier 状态指示器 ──
// 对齐 Claude Code auto mode / Cursor Auto-review：
// - auto 模式：显示 classifier 生效状态与 blocked 计数进度（3连/20次阈值）；
// - 触发回退：显示警示 chip + 「恢复自动」按钮（重置计数并回到 auto）。
import { SafetyCertificateOutlined, WarningOutlined } from '@ant-design/icons';
import { Popover, Tag, Tooltip } from 'antd';
import React from 'react';
import type { AutoReviewClassifierState } from '../classifier/autoReviewClassifier';
import { AUTO_REVIEW_THRESHOLDS } from '../hooks/useAutoReviewClassifier';
import RecentlyDeniedPanel from './RecentlyDeniedPanel';
import type { RecentlyDeniedItem } from '../classifier/autoReviewClassifier';

interface AutoReviewIndicatorProps {
  /** classifier 状态 */
  state: AutoReviewClassifierState;
  /** Recently denied 面板条目 */
  recentlyDenied?: RecentlyDeniedItem[];
  /** 是否禁用（如未选择工作空间/正在生成） */
  disabled?: boolean;
  /** 触发回退时展示的说明（默认使用内置文案） */
  fallbackLabel?: string;
  /** 恢复自动回调 */
  onRestoreAuto?: () => void;
  /** 重试 Recently denied 条目 */
  onRetryDenied?: (item: RecentlyDeniedItem) => void;
  /** 移除 Recently denied 条目 */
  onRemoveDenied?: (id: string) => void;
  /** 清空 Recently denied */
  onClearDenied?: () => void;
}

const chipBase: React.CSSProperties = {
  display: 'inline-flex',
  alignItems: 'center',
  gap: 5,
  height: 34,
  padding: '0 9px',
  border: 'none',
  borderRadius: 17,
  fontSize: 12,
  cursor: 'pointer',
  transition: 'background 140ms ease, color 140ms ease',
};

const AutoReviewIndicator: React.FC<AutoReviewIndicatorProps> = ({
  state,
  recentlyDenied = [],
  disabled = false,
  fallbackLabel,
  onRestoreAuto,
  onRetryDenied,
  onRemoveDenied,
  onClearDenied,
}) => {
  const blocked = state.totalBlocks > 0;
  const progressText = state.enabled
    ? `${state.consecutiveBlocks}/${AUTO_REVIEW_THRESHOLDS.consecutive}连 · ${state.totalBlocks}/${AUTO_REVIEW_THRESHOLDS.total}次`
    : null;

  const chipContent = (() => {
    if (state.fallbackTriggered) {
      return (
        <span
          style={{
            ...chipBase,
            background: 'color-mix(in srgb, #c4944c 16%, transparent)',
            color: '#9a6b1f',
          }}
          role="status"
          data-testid="auto-review-fallback-chip"
          aria-label="已回退手动审批"
        >
          <WarningOutlined />
          <span>{fallbackLabel ?? '已回退手动审批'}</span>
        </span>
      );
    }
    if (disabled) {
      return (
        <span
          style={{
            ...chipBase,
            background: 'transparent',
            color: 'var(--pudding-chat-text-muted)',
            opacity: 0.45,
          }}
          data-testid="auto-review-disabled-chip"
        >
          <SafetyCertificateOutlined />
          <span>Auto-review</span>
        </span>
      );
    }
    return (
      <span
        style={{
          ...chipBase,
          background: blocked
            ? 'color-mix(in srgb, #b85656 12%, transparent)'
            : 'color-mix(in srgb, var(--pudding-chat-accent, #8b5cf6) 9%, transparent)',
          color: blocked ? '#b04040' : 'var(--pudding-chat-text-muted)',
        }}
        data-testid="auto-review-chip"
      >
        <SafetyCertificateOutlined />
        <span>{state.enabled ? 'Auto-review' : 'Manual'}</span>
        {progressText && (
          <Tag
            color={blocked ? 'error' : 'default'}
            style={{ marginInlineEnd: 0, fontSize: 10, lineHeight: '16px' }}
            data-testid="auto-review-progress"
          >
            {progressText}
          </Tag>
        )}
      </span>
    );
  })();

  if (state.fallbackTriggered) {
    return (
      <span
        style={{ display: 'inline-flex', alignItems: 'center', gap: 6 }}
        data-testid="auto-review-fallback"
      >
        {chipContent}
        <button
          type="button"
          style={{
            ...chipBase,
            background: 'color-mix(in srgb, var(--pudding-chat-accent, #8b5cf6) 12%, transparent)',
            color: 'var(--pudding-chat-accent, #8b5cf6)',
            fontWeight: 500,
          }}
          onClick={onRestoreAuto}
          disabled={disabled}
          data-testid="auto-review-restore"
        >
          恢复自动
        </button>
      </span>
    );
  }

  return (
    <Popover
      trigger="click"
      placement="topRight"
      content={
        <RecentlyDeniedPanel
          items={recentlyDenied}
          onRetry={onRetryDenied}
          onRemove={onRemoveDenied}
          onClear={onClearDenied}
        />
      }
    >
      <Tooltip
        title={
          state.enabled
            ? 'Auto-review：高风险动作自动预审，连续拦截或累计拦截达到阈值后回退手动审批'
            : '当前不在 auto 模式，classifier 未生效'
        }
      >
        <span style={{ display: 'inline-flex' }}>{chipContent}</span>
      </Tooltip>
    </Popover>
  );
};

export default React.memo(AutoReviewIndicator);
