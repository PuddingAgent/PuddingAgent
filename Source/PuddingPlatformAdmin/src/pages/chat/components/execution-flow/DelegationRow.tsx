// ── DelegationRow：父级委派摘要折叠行（CU-09，Phase A / TR-05 前置）────────
// 主消息只显示父级委派事实：
//  - 折叠态：「子代理 · N 个运行中/已完成/失败」+ 最长运行时间（估算）
//  - 展开态：每子代理仅显示任务摘要、模型、状态 + 「打开检查器」入口
//  - 不复制子代理内部 reasoning / tool / 完整结果（由 SubAgentActivityDock 与
//    运行检查器承载，主消息零重复）
//  - 空 DelegationNode[] → null（无子代理不渲染）
// 复用 ExecutionDisclosureRow 行式 chrome（16px leading 槽 / chevron 占位稳定 /
// 整行可点 ≥32px / Enter+Space 键盘展开 / 展开体 32px 缩进；CU-05 §5.1 + §6.1）。
//
// 事实源：CU-04 ExecutionFlowProjector 输出的 DelegationNode（subagent.spawned →
// running，subagent.completed → completed/failed；subAgentId 为 canonical key）。
// 消费点适配：buildDelegationNodesFromProcessItems 把 TimelineItem[]（含
// subagent_spawned/progress/completed 条目）转换为 DelegationNode[]——对齐 CU-07
// ToolCallTree 的 buildToolTreeFromProcessItems 模式，刷新后按 subAgentId 恢复。
//
// 纯度约束：无 DOM 副作用 / 无 Store 突变 / 无时间源伪造。运行时长基于持久化
// occurredAt 与当前时间差估算（缺失时间源时只取状态文案，不编造精确数值）。
import { createStyles } from 'antd-style';
import React, { useEffect, useState } from 'react';
import type { DelegationNode } from '../../projections/executionFlowProjector';
import type { TimelineItem } from '../../types';
import StateDot, { type StateDotState } from '../StateDot';
import { ExecutionDisclosureRow } from './ExecutionDisclosureRow';

// ── 消费点适配：TimelineItem[] → DelegationNode[]（对齐 CU-07 适配模式）────

const isSubAgentTimelineItem = (item: TimelineItem): boolean =>
  item.type === 'subagent_spawned' ||
  item.type === 'subagent_progress' ||
  item.type === 'subagent_completed';

const toneOf = (node: DelegationNode): 'running' | 'success' | 'error' =>
  node.state === 'running'
    ? 'running'
    : node.state === 'failed'
      ? 'error'
      : 'success';

/** 状态 tone 派生（对齐 processPreview.getToolStatusTone 语义，避免跨模块重复依赖）。 */
const deriveTone = (
  item: TimelineItem,
): 'running' | 'success' | 'error' => {
  const status = (item.status ?? '').toLowerCase();
  if (
    status.includes('error') ||
    status.includes('fail') ||
    status.includes('cancel')
  ) {
    return 'error';
  }
  if (
    status.includes('success') ||
    status.includes('done') ||
    status.includes('complete')
  ) {
    return 'success';
  }
  return 'running';
};

/**
 * 从 processItems（TimelineItem[]）构建 DelegationNode[]。
 *  - subagent_spawned：running 节点（taskSummary 取 text/message）
 *  - subagent_progress：保留 running，合并最新进展摘要
 *  - subagent_completed：终态（success=false → failed，否则 completed）
 * 按 subAgentId（name）精确聚合；无 subagent 条目时返回空数组。
 * 纯函数：同输入深度一致，刷新后按 subAgentId 恢复终态摘要。
 */
export const buildDelegationNodesFromProcessItems = (
  items: TimelineItem[],
): DelegationNode[] => {
  const byId = new Map<string, DelegationNode>();
  const sequenceOrder: string[] = [];

  const ensureNode = (item: TimelineItem, subAgentId: string): DelegationNode => {
    const existing = byId.get(subAgentId);
    if (existing) return existing;
    const node: DelegationNode = {
      kind: 'delegation',
      key: `delegation:${subAgentId}`,
      firstEventId: item.eventId,
      sequence: sequenceOrder.length,
      occurredAt:
        Number.isFinite(item.timestamp) && item.timestamp > 0
          ? new Date(item.timestamp).toISOString()
          : undefined,
      sourceEventIds: item.eventId ? [item.eventId] : [],
      subAgentId,
      state: 'running',
    };
    byId.set(subAgentId, node);
    sequenceOrder.push(subAgentId);
    return node;
  };

  for (const item of items) {
    if (!isSubAgentTimelineItem(item)) continue;
    const subAgentId = item.name?.trim() || item.id;
    if (!subAgentId) continue;
    const node = ensureNode(item, subAgentId);

    if (item.type === 'subagent_spawned') {
      node.taskSummary = item.text || item.message || node.taskSummary;
      if (item.eventId) node.sourceEventIds.push(item.eventId);
      continue;
    }
    if (item.type === 'subagent_progress') {
      const progress = item.text || item.message || '';
      if (progress) node.taskSummary = progress;
      if (item.eventId) node.sourceEventIds.push(item.eventId);
      continue;
    }
    // subagent_completed：终态单调（running → completed/failed 一次）。
    if (node.state === 'running') {
      const tone = deriveTone(item);
      node.state = tone === 'error' ? 'failed' : 'completed';
      node.success = tone !== 'error';
    }
    node.replySummary = item.output || item.message || node.replySummary;
    if (toneOf(node) === 'error' && item.message) node.error = item.message;
    if (item.eventId) node.sourceEventIds.push(item.eventId);
  }

  return sequenceOrder
    .map((subAgentId) => byId.get(subAgentId))
    .filter((node): node is DelegationNode => Boolean(node));
};

// ── 折叠态文案与最长运行时间 ──────────────────────────────────────────────

/** 「Xs」/「Xm」：<60s 显示秒，≥60s 取整分钟（与 TurnStatus 同格式）。 */
export function formatDelegationElapsed(seconds: number): string {
  return seconds < 60 ? `${seconds}s` : `${Math.floor(seconds / 60)}m`;
}

/** 状态文案（展开态每子代理行 / 折叠计数共用）。 */
const STATUS_LABEL: Record<DelegationNode['state'], string> = {
  running: '运行中',
  completed: '已完成',
  failed: '失败',
};

const STATUS_DOT: Record<DelegationNode['state'], StateDotState> = {
  running: 'ongoing',
  completed: 'done',
  failed: 'error',
};

// ── 组件级样式（不触碰 CU-05/06/07 已提交样式文件，仅新增 delegation 专属 token）──

const useDelegationStyles = createStyles(() => ({
  /** 折叠态标题：13px/20px 600（对齐 turnStatusLabel 规格） */
  title: {
    fontSize: 13,
    fontWeight: 600,
    lineHeight: '20px',
    color: 'var(--pudding-chat-text)',
    whiteSpace: 'nowrap' as const,
  },
  /** 最长运行时长：11px 次级（对齐 turnStatusElapsed） */
  elapsed: {
    color: 'var(--pudding-chat-text-muted)',
    fontSize: 11,
    lineHeight: '20px',
    fontVariantNumeric: 'tabular-nums' as const,
    whiteSpace: 'nowrap' as const,
  },
  /** 展开态列表：纵向排布 */
  list: {
    display: 'flex',
    flexDirection: 'column' as const,
    gap: 6,
  },
  /** 每子代理行：状态点 + 摘要 + 模型 + 状态 + 检查器入口 */
  item: {
    display: 'flex',
    alignItems: 'center',
    gap: 8,
    minHeight: 28,
    padding: '4px 8px',
    boxSizing: 'border-box' as const,
    borderRadius: 6,
    background: 'var(--pudding-chat-surface-muted)',
  },
  /** 任务摘要：flex 填充单行 ellipsis */
  summary: {
    flex: 1,
    minWidth: 0,
    fontSize: 12,
    lineHeight: '20px',
    color: 'var(--pudding-chat-text)',
    whiteSpace: 'nowrap' as const,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
  },
  /** 模型：小号等宽 tag */
  model: {
    flexShrink: 0,
    fontSize: 10,
    lineHeight: '16px',
    color: 'var(--pudding-chat-text-muted)',
    background:
      'color-mix(in srgb, var(--pudding-chat-text-subtle) 12%, transparent)',
    borderRadius: 4,
    padding: '0 6px',
    whiteSpace: 'nowrap' as const,
  },
  /** 状态标签：10px 语义色 */
  status: {
    flexShrink: 0,
    fontSize: 10,
    lineHeight: '16px',
    whiteSpace: 'nowrap' as const,
  },
  statusRunning: { color: 'var(--pudding-status-running)' },
  statusCompleted: { color: 'var(--pudding-status-success)' },
  statusFailed: { color: 'var(--pudding-status-error)' },
  /** 检查器入口：小号次级按钮 */
  inspectorBtn: {
    flexShrink: 0,
    fontSize: 11,
    lineHeight: '18px',
    color: 'var(--pudding-chat-text)',
    background:
      'color-mix(in srgb, var(--pudding-chat-text-subtle) 10%, transparent)',
    border:
      '1px solid color-mix(in srgb, var(--pudding-chat-text-subtle) 24%, transparent)',
    borderRadius: 6,
    padding: '1px 8px',
    cursor: 'pointer',
    userSelect: 'none' as const,
    '&:hover': {
      background:
        'color-mix(in srgb, var(--pudding-chat-text-subtle) 16%, transparent)',
    },
    '&:focus-visible': {
      outline: '2px solid var(--pudding-status-running)',
      outlineOffset: -2,
    },
  },
}));

// ── DelegationRow 组件 ─────────────────────────────────────────────────────

export interface DelegationRowProps {
  /** 有序委派节点（来自 projector / 消费点适配）；空数组 → null。 */
  nodes: DelegationNode[];
  /** 打开检查器入口（runId/subAgentId → SubAgentActivityDock 检查器）。 */
  onOpenInspector?: (runId: string) => void;
  /** 测试注入当前时间（毫秒）；未传时 running 态每秒 tick（对齐 TurnStatus）。 */
  now?: number;
}

export const DelegationRow: React.FC<DelegationRowProps> = ({
  nodes,
  onOpenInspector,
  now: nowProp,
}) => {
  const { styles, cx } = useDelegationStyles();
  const [now, setNow] = useState(() => nowProp ?? Date.now());

  // 仅当存在 running 节点时每秒 tick（时长估算；终态摘要不依赖 tick）。
  const hasRunning = nodes.some((node) => node.state === 'running');
  useEffect(() => {
    if (nowProp !== undefined) return undefined;
    if (!hasRunning) return undefined;
    const timer = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(timer);
  }, [hasRunning, nowProp]);

  // 无子代理 → 不渲染（验收 3/4）。
  if (nodes.length === 0) return null;

  // 折叠态计数：子代理 · N 个运行中/M 个已完成/K 个失败（零项省略）。
  const running = nodes.filter((node) => node.state === 'running').length;
  const completed = nodes.filter((node) => node.state === 'completed').length;
  const failed = nodes.filter((node) => node.state === 'failed').length;
  const countParts: string[] = [];
  if (running > 0) countParts.push(`${running} 个运行中`);
  if (completed > 0) countParts.push(`${completed} 个已完成`);
  if (failed > 0) countParts.push(`${failed} 个失败`);
  const countText = countParts.length > 0 ? countParts.join('/') : '0 个运行中';

  // 最长运行时间：仅 running 节点且有 occurredAt 时估算（无时间源不伪造）。
  let longestElapsed: string | null = null;
  if (running > 0) {
    const nowMs = nowProp ?? now;
    let longestSeconds = 0;
    let found = false;
    for (const node of nodes) {
      if (node.state !== 'running') continue;
      const startedAt = node.occurredAt
        ? Date.parse(node.occurredAt)
        : Number.NaN;
      if (!Number.isFinite(startedAt) || startedAt <= 0) continue;
      found = true;
      longestSeconds = Math.max(
        longestSeconds,
        Math.max(0, Math.floor((nowMs - startedAt) / 1000)),
      );
    }
    if (found) longestElapsed = formatDelegationElapsed(longestSeconds);
  }

  const hasInspector = typeof onOpenInspector === 'function';

  return (
    <ExecutionDisclosureRow
      leading={<StateDot state={hasRunning ? 'ongoing' : 'done'} size={10} />}
      testId="delegation-row"
      ariaLabel="子代理委派摘要"
      expandedContent={
        <div
          className={styles.list}
          data-testid="delegation-list"
        >
          {nodes.map((node) => {
            const tone = toneOf(node);
            const statusClass =
              tone === 'error'
                ? styles.statusFailed
                : tone === 'success'
                  ? styles.statusCompleted
                  : styles.statusRunning;
            return (
              <div
                key={node.key}
                className={styles.item}
                data-testid={`delegation-item-${node.subAgentId}`}
              >
                <StateDot state={STATUS_DOT[node.state]} size={10} />
                <span
                  className={styles.summary}
                  title={node.taskSummary || node.replySummary}
                >
                  {node.taskSummary || node.replySummary || '子代理'}
                </span>
                {node.model && (
                  <span className={styles.model} data-testid="delegation-model">
                    {node.model}
                  </span>
                )}
                <span className={cx(styles.status, statusClass)}>
                  {STATUS_LABEL[node.state]}
                </span>
                {hasInspector && (
                  <button
                    type="button"
                    className={styles.inspectorBtn}
                    data-testid={`delegation-inspector-${node.subAgentId}`}
                    onClick={() => onOpenInspector(node.subAgentId)}
                  >
                    打开检查器
                  </button>
                )}
              </div>
            );
          })}
        </div>
      }
    >
      <span className={styles.title} data-testid="delegation-title">
        子代理 · {countText}
      </span>
      {longestElapsed && (
        <span className={styles.elapsed} data-testid="delegation-elapsed">
          · 最长运行 {longestElapsed}
        </span>
      )}
    </ExecutionDisclosureRow>
  );
};

export default React.memo(DelegationRow);
