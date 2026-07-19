import {
  CheckCircleOutlined,
  CloseCircleOutlined,
  LoadingOutlined,
  RightOutlined,
  RobotOutlined,
} from '@ant-design/icons';
import { createStyles } from 'antd-style';
import React, { useMemo } from 'react';
import type { SubAgentCard } from '../types';

interface SubAgentAnchorProps {
  cards: SubAgentCard[];
  onOpen?: (runId?: string) => void;
}

const useStyles = createStyles(() => ({
  anchor: {
    width: 'calc(100% - 64px)',
    minHeight: 42,
    margin: '8px 16px 8px 48px',
    padding: '8px 12px',
    display: 'flex',
    alignItems: 'center',
    gap: 10,
    border: '1px solid var(--pudding-chat-border)',
    borderRadius: 10,
    background:
      'color-mix(in srgb, var(--pudding-chat-surface) 82%, transparent)',
    color: 'var(--pudding-chat-text-subtle)',
    textAlign: 'left' as const,
    cursor: 'pointer',
    transition: 'border-color 150ms ease, background 150ms ease',
    '&:hover': {
      borderColor:
        'color-mix(in srgb, var(--pudding-chat-accent) 38%, var(--pudding-chat-border))',
      background:
        'color-mix(in srgb, var(--pudding-chat-accent-soft) 38%, var(--pudding-chat-surface))',
    },
    '&:focus-visible': {
      outline: '2px solid var(--pudding-chat-accent)',
      outlineOffset: 2,
    },
  },
  icon: {
    width: 26,
    height: 26,
    flex: '0 0 26px',
    display: 'inline-flex',
    alignItems: 'center',
    justifyContent: 'center',
    borderRadius: 8,
    color: 'var(--pudding-chat-accent)',
    background: 'var(--pudding-chat-accent-soft)',
  },
  copy: {
    minWidth: 0,
    flex: 1,
  },
  title: {
    fontSize: 12,
    lineHeight: '17px',
    fontWeight: 600,
    color: 'var(--pudding-chat-text)',
  },
  detail: {
    marginTop: 1,
    overflow: 'hidden',
    whiteSpace: 'nowrap' as const,
    textOverflow: 'ellipsis',
    fontSize: 11,
    lineHeight: '15px',
    color: 'var(--pudding-chat-text-subtle)',
  },
  tail: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: 6,
    fontSize: 11,
    whiteSpace: 'nowrap' as const,
  },
}));

const activeStatuses = new Set(['spawning', 'running']);
const failureStatuses = new Set([
  'failed',
  'cancelled',
  'timed_out',
  'interrupted',
]);

const SubAgentAnchor: React.FC<SubAgentAnchorProps> = ({ cards, onOpen }) => {
  const { styles } = useStyles();
  const summary = useMemo(() => {
    const running = cards.filter((card) =>
      activeStatuses.has(card.status),
    ).length;
    const completed = cards.filter(
      (card) => card.status === 'completed',
    ).length;
    const failed = cards.filter((card) =>
      failureStatuses.has(card.status),
    ).length;
    return { running, completed, failed };
  }, [cards]);

  const title =
    summary.running > 0
      ? `已启动 ${cards.length} 个子代理 · ${summary.running} 个运行中`
      : `子代理执行结束 · ${summary.completed} 完成${
          summary.failed ? ` · ${summary.failed} 异常` : ''
        }`;
  const detail = cards
    .map((card) => card.originToolId ?? card.role ?? card.taskSummary)
    .join('、');
  const stateIcon =
    summary.running > 0 ? (
      <LoadingOutlined spin />
    ) : summary.failed > 0 ? (
      <CloseCircleOutlined style={{ color: 'var(--ant-color-error)' }} />
    ) : (
      <CheckCircleOutlined style={{ color: 'var(--ant-color-success)' }} />
    );

  return (
    <button
      type="button"
      className={styles.anchor}
      onClick={() => onOpen?.(cards[0]?.runId)}
      aria-label={`${title}，查看子代理详情`}
      data-testid="subagent-anchor"
    >
      <span className={styles.icon}>
        <RobotOutlined />
      </span>
      <span className={styles.copy}>
        <span className={styles.title}>{title}</span>
        <span className={styles.detail}>{detail}</span>
      </span>
      <span className={styles.tail}>
        {stateIcon}
        <span>查看</span>
        <RightOutlined />
      </span>
    </button>
  );
};

export default SubAgentAnchor;
