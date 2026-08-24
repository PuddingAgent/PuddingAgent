// ── ExecutionFlowTimeline：按真实发生顺序交错的行为链时间线（行为链升级 P2）──
// 渲染顺序 = canonical sequence 顺序：文本段 → reasoning 段 → 工具（含子树）→
// 文本段 → 委派 → …，对齐 deepseek-harness「单条 assistant message 多 block 按
// 出现顺序交错 + 每 step 一条消息节点」与 Claude interleaved thinking。
// 正文分段（MessageNode）按段内联渲染；尾段（其后仅剩 terminal 的最后一段）
// 由 AgentMessageBubble 的正文气泡/打字机承载，时间线跳过，杜绝同段双渲染。
// 终态（terminal）由错误行/StatsLine 既有承载，不进时间线。
//
// 数据源（§3.4）：
//  - 路径 B：projection（ExecutionFlowProjector 输出）nodes 的 sequence 顺序；
//  - 路径 A 回退：processItems（TimelineItem[]）按时间序分组为同样的有序 entry，
//    两条路径共享同一渲染结构（buildEntriesFromProcessItems adapter）。
//
// 纯度约束：adapter 为纯函数；reasoning 段「当前段」判定只用输入事实
// （run 活跃 && 尾部连续 reasoning），不引入时间源；段时长全部来自服务端
// occurredAt / timestamp 差，缺失不伪造。
import React from 'react';
import type {
  DelegationNode,
  ExecutionFlowProjection,
  MessageNode,
  ToolNode,
} from '../../projections/executionFlowProjector';
import type { TimelineItem } from '../../types';
import { sanitizeProcessText } from '../processPreview';
import { useExecutionFlowStyles } from '../../styles/execution-flow.styles';
import MessageItem from '../MessageItem';
import {
  buildDelegationNodesFromProcessItems,
  DelegationRow,
} from './DelegationRow';
import { ReasoningDisclosureRow } from './ReasoningDisclosureRow';
import { isPlaceholderVoid, ToolCallTreeBranch } from './ToolCallTree';
import {
  buildToolNodesFromProcessItems,
  buildToolTreeFromProcessItems,
} from './ToolCallTree';

// ── 统一有序 entry（两路径共享的 ViewModel）──────────────────────────────

export interface ReasoningTimelineEntry {
  kind: 'reasoning';
  key: string;
  lines: { id: string; text: string }[];
  /** run 活跃且该段是尾部段（其后无工具/委派）→ 摘要跟随最新行 + 行扫光。 */
  isCurrent: boolean;
  /** 段时长（服务端事实派生）；缺失 → null（不渲染计量 chip）。 */
  durationMs: number | null;
}

export interface MessageTimelineEntry {
  kind: 'message';
  key: string;
  node: MessageNode;
}

export interface ToolTimelineEntry {
  kind: 'tool';
  key: string;
  node: ToolNode;
}

export interface DelegationTimelineEntry {
  kind: 'delegation';
  key: string;
  nodes: DelegationNode[];
}

export type ExecutionTimelineEntry =
  | ReasoningTimelineEntry
  | MessageTimelineEntry
  | ToolTimelineEntry
  | DelegationTimelineEntry;

/**
 * 尾段判定（纯函数）：最后一个含正文的 message 节点，且其后只剩 terminal
 * 节点。尾段是「正在流式/最终回答」的承载者，由 AgentMessageBubble 正文
 * 气泡渲染；非尾段（后面还有工具/委派/推理）在时间线内联渲染。
 */
export const deriveTrailingMessageNode = (
  projection: ExecutionFlowProjection,
): MessageNode | undefined => {
  for (let i = projection.nodes.length - 1; i >= 0; i--) {
    const node = projection.nodes[i];
    if (node.kind === 'terminal') continue;
    if (node.kind === 'message') {
      return node.text.trim().length > 0 ? node : undefined;
    }
    return undefined;
  }
  return undefined;
};

/** occurredAt（ISO 字符串）差 → 毫秒；任一缺失/非法 → null。 */
const diffIsoMs = (first?: string, last?: string): number | null => {
  if (!first || !last) return null;
  const a = Date.parse(first);
  const b = Date.parse(last);
  if (!Number.isFinite(a) || !Number.isFinite(b) || b < a) return null;
  return b - a;
};

/** run 活跃时，尾部连续 reasoning entry 标记为当前段（其后出现的非 reasoning 已完成）。 */
const markTrailingReasoningCurrent = (
  entries: ExecutionTimelineEntry[],
  isRunActive: boolean,
): void => {
  if (!isRunActive) return;
  for (let i = entries.length - 1; i >= 0; i--) {
    const entry = entries[i];
    if (entry.kind !== 'reasoning') break;
    entry.isCurrent = true;
  }
};

/**
 * 路径 B：投影 nodes → 有序 entry。
 * terminal/retry 不进时间线（错误行/StatsLine/ModelRetryRow 既有承载）；
 * message 段按 messageSegments 配置决定是否内联（尾段 key 跳过，由正文气泡承载）。
 */
export const buildEntriesFromProjection = (
  projection: ExecutionFlowProjection,
  isRunActive: boolean,
  messageSegments?: { trailingKey?: string },
): ExecutionTimelineEntry[] => {
  const entries: ExecutionTimelineEntry[] = [];
  for (const node of projection.nodes) {
    if (node.kind === 'reasoning') {
      const text = sanitizeProcessText(node.text, { compact: false });
      if (!text) continue;
      entries.push({
        kind: 'reasoning',
        key: node.key,
        lines:
          node.blocks.length > 0
            ? node.blocks.map((block) => ({
                id: block.id,
                text: block.text,
              }))
            : [{ id: node.key, text }],
        isCurrent: false,
        durationMs: diffIsoMs(node.occurredAt, node.lastOccurredAt),
      });
      continue;
    }
    if (node.kind === 'message') {
      if (!messageSegments || !node.text.trim()) continue;
      if (node.key === messageSegments.trailingKey) continue;
      entries.push({ kind: 'message', key: node.key, node });
      continue;
    }
    if (node.kind === 'tool') {
      // 投影器已建树：nodes 顶层只保留根；占位空壳跳过（同 ToolCallTree）。
      if (isPlaceholderVoid(node)) continue;
      entries.push({ kind: 'tool', key: node.key, node });
      continue;
    }
    if (node.kind === 'delegation') {
      const previous = entries[entries.length - 1];
      if (previous?.kind === 'delegation') {
        previous.nodes.push(node);
      } else {
        entries.push({
          kind: 'delegation',
          key: `delegation-group:${node.key}`,
          nodes: [node],
        });
      }
    }
  }
  markTrailingReasoningCurrent(entries, isRunActive);
  return entries;
};

/** 路径 A：processItems（按时间序 TimelineItem[]）→ 同一有序 entry 结构。 */
export const buildEntriesFromProcessItems = (
  items: TimelineItem[],
  isRunActive: boolean,
): ExecutionTimelineEntry[] => {
  // 工具：配对 + 建树（复用既有 adapter 语义），根锚定在其 tool_call 位置。
  const roots = buildToolTreeFromProcessItems(items);
  const rootByCallId = new Map<string, ToolNode>();
  for (const root of roots) rootByCallId.set(root.toolCallId, root);

  // 委派：按 subAgentId 聚合的节点集合；首次 spawn 位置作为渲染锚点。
  const delegationNodes = buildDelegationNodesFromProcessItems(items);
  const delegationById = new Map<string, DelegationNode>();
  for (const node of delegationNodes) delegationById.set(node.subAgentId, node);
  const spawnedDelegationIds = new Set<string>();

  const entries: ExecutionTimelineEntry[] = [];
  let openReasoning: {
    key: string;
    lines: { id: string; text: string }[];
    firstTs: number;
    lastTs: number;
  } | null = null;

  const flushReasoning = () => {
    if (!openReasoning) return;
    entries.push({
      kind: 'reasoning',
      key: openReasoning.key,
      lines: openReasoning.lines,
      isCurrent: false,
      durationMs:
        openReasoning.lastTs >= openReasoning.firstTs
          ? openReasoning.lastTs - openReasoning.firstTs
          : null,
    });
    openReasoning = null;
  };

  for (const item of items) {
    if (item.type === 'thinking') {
      const text = sanitizeProcessText(item.text, { compact: false });
      if (!text.trim()) continue;
      if (!openReasoning) {
        openReasoning = {
          key: `reasoning:${item.id}`,
          lines: [],
          firstTs: item.timestamp,
          lastTs: item.timestamp,
        };
      }
      openReasoning.lines.push({ id: item.id, text });
      openReasoning.lastTs = item.timestamp;
      continue;
    }
    flushReasoning();
    if (item.type === 'tool_call' && item.toolCallId) {
      const root = rootByCallId.get(item.toolCallId);
      if (root) {
        entries.push({ kind: 'tool', key: root.key, node: root });
        rootByCallId.delete(item.toolCallId);
      }
      continue;
    }
    if (item.type === 'subagent_spawned') {
      const subAgentId = item.name?.trim() || item.id;
      const node = delegationById.get(subAgentId);
      if (!node || spawnedDelegationIds.has(subAgentId)) continue;
      spawnedDelegationIds.add(subAgentId);
      const previous = entries[entries.length - 1];
      if (previous?.kind === 'delegation') {
        previous.nodes.push(node);
      } else {
        entries.push({
          kind: 'delegation',
          key: `delegation-group:${node.key}`,
          nodes: [node],
        });
      }
    }
  }
  flushReasoning();
  markTrailingReasoningCurrent(entries, isRunActive);
  return entries;
};

/** 时间线统计（TurnStatsLine 数据源）：段数 / 工具数（含子调用）。 */
export interface TimelineTurnStats {
  reasoningSegments: number;
  toolCount: number;
}

const countToolsRecursive = (nodes: readonly ToolNode[]): number => {
  let count = 0;
  for (const node of nodes) {
    if (isPlaceholderVoid(node)) continue;
    count += 1 + countToolsRecursive(node.children);
  }
  return count;
};

/** 从投影统计（路径 B）。投影 nodes 顶层只含工具树根（子调用在 children）。 */
export const deriveStatsFromProjection = (
  projection: ExecutionFlowProjection,
): TimelineTurnStats => {
  let reasoningSegments = 0;
  let toolCount = 0;
  for (const node of projection.nodes) {
    if (node.kind === 'reasoning' && node.text.trim()) reasoningSegments += 1;
    if (node.kind === 'tool') {
      toolCount += 1 + countToolsRecursive(node.children);
    }
  }
  return { reasoningSegments, toolCount };
};

/** 从 processItems 统计（路径 A 回退）。 */
export const deriveStatsFromProcessItems = (
  items: TimelineItem[],
): TimelineTurnStats => {
  let reasoningSegments = 0;
  let inSegment = false;
  for (const item of items) {
    const isThinking = item.type === 'thinking' && item.text?.trim();
    if (isThinking && !inSegment) {
      reasoningSegments += 1;
      inSegment = true;
    } else if (!isThinking) {
      inSegment = false;
    }
  }
  const toolCount = countToolsRecursive(buildToolNodesFromProcessItems(items));
  return { reasoningSegments, toolCount };
};

// ── 组件 ──────────────────────────────────────────────────────────────────

export interface ExecutionFlowTimelineProps {
  /** 路径 B：canonical 投影（提供时优先生效）。 */
  projection?: ExecutionFlowProjection;
  /** 路径 A 回退：processItems（TimelineItem[]，按时间序）。 */
  processItems?: TimelineItem[];
  /** turn 是否仍在运行（尾部 reasoning 段的 running 判定）。 */
  isRunActive?: boolean;
  /**
   * 文本分段内联渲染（行为链交错）：提供时 message 段按序内联渲染，
   * trailingKey 指向的尾段跳过（由 AgentMessageBubble 正文气泡承载）；
   * undefined 时正文整块承载（历史/无投影回退）。
   */
  messageSegments?: { trailingKey?: string };
  /** 委派展开态「打开检查器」入口。 */
  onOpenInspector?: (runId: string) => void;
}

/** 时间线内联文本段：与正文同款 markdown 排版（全宽正文，非过程灰阶）。 */
const MessageSegmentView: React.FC<{ node: MessageNode }> = ({ node }) => {
  const { styles } = useExecutionFlowStyles();
  return (
    <div
      className={styles.timelineMessageSegment}
      data-testid="timeline-message-segment"
    >
      <MessageItem markdownText={node.text} isStreaming={false} />
    </div>
  );
};

/** 交错行为链时间线：无可见 entry → null（不占用布局）。 */
export const ExecutionFlowTimeline: React.FC<ExecutionFlowTimelineProps> = ({
  projection,
  processItems,
  isRunActive = false,
  messageSegments,
  onOpenInspector,
}) => {
  const { styles } = useExecutionFlowStyles();
  const entries = React.useMemo(() => {
    if (projection)
      return buildEntriesFromProjection(
        projection,
        isRunActive,
        messageSegments,
      );
    return buildEntriesFromProcessItems(processItems ?? [], isRunActive);
  }, [projection, processItems, isRunActive, messageSegments]);

  if (entries.length === 0) return null;

  return (
    <div
      className={styles.timelineList}
      data-testid="execution-flow-timeline"
    >
      {entries.map((entry) => {
        if (entry.kind === 'reasoning') {
          return (
            <ReasoningDisclosureRow
              key={entry.key}
              lines={entry.lines}
              isCurrent={entry.isCurrent}
              durationMs={entry.durationMs}
            />
          );
        }
        if (entry.kind === 'message') {
          return <MessageSegmentView key={entry.key} node={entry.node} />;
        }
        if (entry.kind === 'tool') {
          return <ToolCallTreeBranch key={entry.key} node={entry.node} />;
        }
        return (
          <DelegationRow
            key={entry.key}
            nodes={entry.nodes}
            onOpenInspector={onOpenInspector}
          />
        );
      })}
    </div>
  );
};

export default React.memo(ExecutionFlowTimeline);
