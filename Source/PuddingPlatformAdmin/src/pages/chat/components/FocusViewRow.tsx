// ── FocusViewRow：Focus view 单行折叠行（P2#8）──────────────
// 每个 turn 折叠为一行：头像 + 名称 + 单行摘要（运行中显示当前工具）+ 时间 + 展开箭头。
// 点击展开后渲染完整消息内容（children = 常规气泡渲染）。
import { DownOutlined, UpOutlined } from '@ant-design/icons';
import React from 'react';
import { useChatMessageStyles } from '../styles/messageStyleContext';
import AgentAvatar from './AgentAvatar';

export type FocusViewRowTone = 'running' | 'error' | 'done';

interface FocusViewRowProps {
  role: 'user' | 'agent';
  name: string;
  avatarEmoji?: string;
  avatarColor?: string;
  avatarUrl?: string;
  timeText: string;
  summary: string;
  tone?: FocusViewRowTone;
  expanded: boolean;
  onToggle: () => void;
  children?: React.ReactNode;
}

const FocusViewRow: React.FC<FocusViewRowProps> = ({
  role,
  name,
  avatarEmoji,
  avatarColor,
  avatarUrl,
  timeText,
  summary,
  tone = 'done',
  expanded,
  onToggle,
  children,
}) => {
  const { styles, cx } = useChatMessageStyles();
  return (
    <div
      className={styles.focusViewRow}
      data-testid="focus-view-row"
      data-expanded={expanded ? 'true' : 'false'}
    >
      <button
        type="button"
        className={cx(
          styles.focusViewRowHeader,
          tone === 'running' && styles.focusViewRowHeaderRunning,
          tone === 'error' && styles.focusViewRowHeaderError,
        )}
        onClick={onToggle}
        aria-expanded={expanded}
        aria-label={expanded ? '收起完整内容' : '展开完整内容'}
        data-testid="focus-view-row-header"
      >
        {role === 'user' ? (
          <span className={styles.focusViewRowUserAvatar} aria-hidden="true">
            我
          </span>
        ) : (
          <AgentAvatar
            name={name}
            emoji={avatarEmoji}
            color={avatarColor}
            imageUrl={avatarUrl}
            grouped={false}
          />
        )}
        <span className={styles.focusViewRowName}>{name}</span>
        <span
          className={cx(
            styles.focusViewRowSummary,
            tone === 'running' && styles.focusViewRowSummaryRunning,
            tone === 'error' && styles.focusViewRowSummaryError,
          )}
          title={summary}
        >
          {summary}
        </span>
        <span className={styles.focusViewRowTime}>{timeText}</span>
        <span className={styles.focusViewRowCaret} aria-hidden="true">
          {expanded ? <UpOutlined /> : <DownOutlined />}
        </span>
      </button>
      {expanded && children ? (
        <div className={styles.focusViewRowContent}>{children}</div>
      ) : null}
    </div>
  );
};

export default React.memo(FocusViewRow);
