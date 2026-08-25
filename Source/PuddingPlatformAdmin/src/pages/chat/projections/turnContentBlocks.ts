// ── turnContentBlocks：Turn 内容块流纯投影（AgentTurnCard 重构 2026-08-25）──
//
// 目标结构（对齐 deepseek-harness AssistantMarkdown 的按 block 顺序渲染）：
//  AgentTurnCard = TextBlock(永久可见) 与 ActivityGroup(可折叠) 按 canonical
//  sequence 交错排列：
//    Text 1 → ActivityGroup 1 → Text 2 → ActivityGroup 2 → …
//  每个最大连续非正文节点序列形成一个 ActivityGroup；不重排、不按类型聚合。
//  正文（message 节点）永久可见且只渲染一次——卡片底部不再有第二个 answer
//  bubble（双区域渲染已退役）。
//
// 纯度约束：本模块只解释 UI 结构，不写回 canonical 事实；无 DOM / 时间源 /
// 副作用。折叠默认值（尾部组展开 / 历史组折叠）由消费层按 block 序决定。
import type {
  DelegationNode,
  ExecutionFlowNode,
  MessageNode,
  ReasoningNode,
  RetryNode,
  ToolNode,
} from './executionFlowProjector';

// ── 块 ViewModel ───────────────────────────────────────────────────────────

/** 参与行为组的节点（正文与 terminal 之外的全部节点）。 */
export type ActivityNode = ReasoningNode | ToolNode | DelegationNode | RetryNode;

/** 正文块：永久可见，卡片内唯一正文源。 */
export interface TextBlock {
  kind: 'text';
  key: string;
  node: MessageNode;
  sequence: number;
}

/** 行为组折叠态摘要（「1 段思考 · 2 次工具 · 18s」的数据源）。 */
export interface ActivityGroupSummary {
  /** 非空 reasoning 节点数（对齐投影统计口径）。 */
  reasoningCount: number;
  /** 工具数（含子调用，占位空壳不计）。 */
  toolCount: number;
  delegationCount: number;
  retryCount: number;
  /** 组内首/末活动发生时间差；任一缺失 → null（不伪造）。 */
  durationMs: number | null;
}

/** 行为组块：两个正文段之间（或首尾）的最大连续非正文节点序列。 */
export interface ActivityGroupBlock {
  kind: 'activity-group';
  /** 组 key = 首个活动节点 key（组内追加节点不改变 key）。 */
  key: string;
  /** 组前最近的正文块 key（组锚定在哪个正文段之后）。 */
  anchorTextKey?: string;
  firstSequence: number;
  lastSequence: number;
  nodes: ActivityNode[];
  /** 组内存在运行中 tool/delegation（占位空壳不算）。 */
  hasRunningNode: boolean;
  summary: ActivityGroupSummary;
}

export type TurnContentBlock = TextBlock | ActivityGroupBlock;

// ── 节点辅助（纯函数）──────────────────────────────────────────────────────

/** 占位空壳工具（result 先于 started 且尚无真实结果）不算运行中。 */
const isPlaceholderVoidTool = (node: ToolNode): boolean =>
  Boolean(
    node.placeholder && !node.output && !node.error && node.exitCode === undefined,
  );

/** 工具节点（含子树）的最大 sequence（result/子调用发生在 requested 之后）。 */
const maxToolSequence = (node: ToolNode): number => {
  let max = node.sequence;
  for (const child of node.children) {
    max = Math.max(max, maxToolSequence(child));
  }
  return max;
};

/** 节点是否处于运行态（reasoning 无状态字段，由消费层按尾部段判定）。 */
export const isRunningActivityNode = (
  node: ActivityNode,
): node is ToolNode | DelegationNode =>
  (node.kind === 'tool' && node.state === 'running' && !isPlaceholderVoidTool(node)) ||
  (node.kind === 'delegation' && node.state === 'running');

const countToolsRecursive = (nodes: readonly ToolNode[]): number => {
  let count = 0;
  for (const node of nodes) {
    if (isPlaceholderVoidTool(node)) continue;
    count += 1 + countToolsRecursive(node.children);
  }
  return count;
};

/** occurredAt（ISO 字符串）差 → 毫秒；任一缺失/非法/倒序 → null。 */
const diffIsoMs = (first?: string, last?: string): number | null => {
  if (!first || !last) return null;
  const a = Date.parse(first);
  const b = Date.parse(last);
  if (!Number.isFinite(a) || !Number.isFinite(b) || b < a) return null;
  return b - a;
};

const buildGroupSummary = (nodes: readonly ActivityNode[]): ActivityGroupSummary => {
  let firstOccurred: string | undefined;
  let lastOccurred: string | undefined;
  for (const node of nodes) {
    if (!node.occurredAt) continue;
    if (!firstOccurred || node.occurredAt < firstOccurred) {
      firstOccurred = node.occurredAt;
    }
    const nodeLast =
      node.kind === 'reasoning'
        ? (node.lastOccurredAt ?? node.occurredAt)
        : node.occurredAt;
    if (!lastOccurred || nodeLast > lastOccurred) {
      lastOccurred = nodeLast;
    }
  }
  return {
    reasoningCount: nodes.filter(
      (node) => node.kind === 'reasoning' && node.text.trim().length > 0,
    ).length,
    toolCount: countToolsRecursive(
      nodes.filter((node): node is ToolNode => node.kind === 'tool'),
    ),
    delegationCount: nodes.filter((node) => node.kind === 'delegation').length,
    retryCount: nodes.filter((node) => node.kind === 'retry').length,
    durationMs: diffIsoMs(firstOccurred, lastOccurred),
  };
};

// ── 主投影：nodes → 内容块流 ───────────────────────────────────────────────

/**
 * 把投影 nodes（canonical sequence 升序）解释为「正文段 ⇄ 行为组」交错的
 * 内容块流。规则：
 *  - message 节点（非空文本）→ TextBlock，并关闭当前行为组；
 *  - 其余非 terminal 节点 → 追加进当前行为组（无则开新组）；
 *  - terminal 节点不进块流（错误行 / StatsLine 既有承载）；
 *  - 不改变节点相对顺序，不按类型聚合。
 */
export function buildTurnContentBlocks(
  nodes: readonly ExecutionFlowNode[],
): TurnContentBlock[] {
  const blocks: TurnContentBlock[] = [];
  let current: ActivityGroupBlock | null = null;
  let anchorTextKey: string | undefined;

  const flushGroup = () => {
    if (!current) return;
    current.summary = buildGroupSummary(current.nodes);
    current = null;
  };

  for (const node of nodes) {
    if (node.kind === 'terminal') continue;
    if (node.kind === 'message') {
      // 空文本段（failed 段的 errorMessage 由错误摘要行承载）不产生正文块。
      if (!node.text.trim()) continue;
      flushGroup();
      anchorTextKey = node.key;
      blocks.push({
        kind: 'text',
        key: node.key,
        node,
        sequence: node.sequence,
      });
      continue;
    }
    if (!current) {
      current = {
        kind: 'activity-group',
        key: `activity:${node.key}`,
        anchorTextKey,
        firstSequence: node.sequence,
        lastSequence: node.sequence,
        nodes: [],
        hasRunningNode: false,
        // 先占位，flushGroup 时按最终成员计算。
        summary: {
          reasoningCount: 0,
          toolCount: 0,
          delegationCount: 0,
          retryCount: 0,
          durationMs: null,
        },
      };
      blocks.push(current);
    }
    current.nodes.push(node);
    current.lastSequence = Math.max(
      current.lastSequence,
      node.kind === 'tool' ? maxToolSequence(node) : node.sequence,
    );
    if (isRunningActivityNode(node)) current.hasRunningNode = true;
  }
  flushGroup();
  return blocks;
}

// ── Turn 终态计量（StatsLine 数据源，自退役的 ExecutionFlowTimeline 迁入）──

export interface TimelineTurnStats {
  reasoningSegments: number;
  toolCount: number;
}

/** 从投影 nodes 统计段数/工具数（顶层工具为树根，子调用在 children）。 */
export function deriveStatsFromProjection(
  nodes: readonly ExecutionFlowNode[],
): TimelineTurnStats {
  let reasoningSegments = 0;
  let toolCount = 0;
  for (const node of nodes) {
    if (node.kind === 'reasoning' && node.text.trim()) reasoningSegments += 1;
    if (node.kind === 'tool') {
      toolCount += 1 + countToolsRecursive(node.children);
    }
  }
  return { reasoningSegments, toolCount };
}
