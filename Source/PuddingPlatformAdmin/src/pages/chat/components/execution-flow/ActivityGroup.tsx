// ── ActivityGroup：TurnContentStream 行为组（AgentTurnCard 重构）────────────
// 两个正文段之间的最大连续非正文节点序列（reasoning/tool/delegation/retry）。
//  - 折叠态：单行「1 段思考 · 2 次工具 · 18s」，成员 DOM 完全卸载（不是 CSS
//    隐藏）——这是历史卡片 DOM/卡顿治理的主手段；
//  - 展开态：成员按 canonical sequence 原序渲染（reasoning 行 / 工具树 /
//    委派行），子代理连续委派聚合为一行；
//  - 受控折叠（注册表）：默认尾部组展开、历史组折叠；组内工具/委派默认保持
//    单行折叠，运行态通过对应行的状态点/扫光表达，避免自动展开大段 IN/OUT；
//    reasoning 行以 mode="inline-full" 内联完整文本自然换行（I-10 §7.8，
//    无二级披露）；用户 override 粘性。
// retry 节点不渲染行（ModelRetryRow / 错误行既有承载），只计入摘要。
import React from 'react';
import type {
  ActivityGroupBlock,
  ActivityNode,
} from '../../projections/turnContentBlocks';
import type { DelegationNode } from '../../projections/executionFlowProjector';
import { useExecutionFlowStyles } from '../../styles/execution-flow.styles';
import { formatDurationMs } from '../../utils/formatDuration';
import StateDot from '../StateDot';
import { DelegationRow } from './DelegationRow';
import { ExecutionDisclosureRow } from './ExecutionDisclosureRow';
import { ReasoningDisclosureRow } from './ReasoningDisclosureRow';
import type { DisclosureRegistry } from './useDisclosureRegistry';
import { isPlaceholderVoid, ToolCallTreeBranch } from './ToolCallTree';

const INITIAL_VISIBLE_ACTIVITY_NODES = 24;
const ACTIVITY_NODE_REVEAL_BATCH = 24;

/** 折叠态摘要文案：「N 段思考 · M 次工具 · K 个子代理 · R 次重试」。 */
export const buildActivityGroupLabel = (
  summary: ActivityGroupBlock['summary'],
): string => {
  const parts: string[] = [];
  if (summary.reasoningCount > 0) parts.push(`${summary.reasoningCount} 段思考`);
  if (summary.toolCount > 0) parts.push(`${summary.toolCount} 次工具`);
  if (summary.delegationCount > 0) parts.push(`${summary.delegationCount} 个子代理`);
  if (summary.retryCount > 0) parts.push(`${summary.retryCount} 次重试`);
  return parts.length > 0 ? parts.join(' · ') : '执行轨迹';
};

const diffIsoMs = (first?: string, last?: string): number | null => {
  if (!first || !last) return null;
  const a = Date.parse(first);
  const b = Date.parse(last);
  if (!Number.isFinite(a) || !Number.isFinite(b) || b < a) return null;
  return b - a;
};

export interface ActivityGroupProps {
  block: ActivityGroupBlock;
  /** 是否为当前块流中最新的行为组：默认展开，组内详情仍保持单行折叠。 */
  isLatestGroup: boolean;
  /** 是否为整个块流的尾块：仅用于判断当前运行态，不决定默认展开。 */
  isTailGroup: boolean;
  /** run 是否仍在运行（尾部连续 reasoning 的当前段判定）。 */
  isRunActive: boolean;
  registry: DisclosureRegistry;
  /** 委派展开态「打开检查器」入口。 */
  onOpenInspector?: (runId: string) => void;
}

export const ActivityGroup: React.FC<ActivityGroupProps> = ({
  block,
  isLatestGroup,
  isTailGroup,
  isRunActive,
  registry,
  onOpenInspector,
}) => {
  const { styles, cx } = useExecutionFlowStyles();
  const [visibleNodeLimit, setVisibleNodeLimit] = React.useState(
    INITIAL_VISIBLE_ACTIVITY_NODES,
  );
  // 默认值：最新行为组始终展开（运行中和完成态一致）；尾部新正文不会把
  // 刚完成的行为轨迹藏掉。只有新行为组出现时，原组才转为历史组。
  // 用户 override 优先且粘性。
  const groupExpanded = registry.isExpanded(block.key, isLatestGroup);
  const label = buildActivityGroupLabel(block.summary);
  const durationText =
    block.summary.durationMs !== null
      ? formatDurationMs(block.summary.durationMs)
      : null;
  const hiddenNodeCount = Math.max(0, block.nodes.length - visibleNodeLimit);
  const visibleNodes =
    hiddenNodeCount > 0 ? block.nodes.slice(hiddenNodeCount) : block.nodes;

  // 尾部组内从末尾起连续 reasoning 且 run 活跃 → 当前段（inline-full 下由
  // StateDot ongoing 表达运行态）。
  const currentNodeKeys = new Set<string>();
  if (isTailGroup && isRunActive) {
    for (let i = block.nodes.length - 1; i >= 0; i--) {
      if (block.nodes[i].kind !== 'reasoning') break;
      currentNodeKeys.add(block.nodes[i].key);
    }
  }

  const renderNodes = (nodes: readonly ActivityNode[]): React.ReactNode => {
    const rows: React.ReactNode[] = [];
    let i = 0;
    while (i < nodes.length) {
      const node = nodes[i];
      if (node.kind === 'reasoning') {
        const text = node.text.trim();
        if (text) {
          const isCurrent = currentNodeKeys.has(node.key);
          const expanded = registry.isExpanded(node.key, false);
          rows.push(
            <ReasoningDisclosureRow
              key={node.key}
              mode="inline-full"
              lines={
                node.blocks.length > 0
                  ? node.blocks.map((blk) => ({ id: blk.id, text: blk.text }))
                  : [{ id: node.key, text }]
              }
              isCurrent={isCurrent}
              durationMs={diffIsoMs(node.occurredAt, node.lastOccurredAt)}
              expanded={expanded}
              onExpandedChange={() => registry.toggle(node.key, expanded)}
            />,
          );
        }
        i += 1;
        continue;
      }
      if (node.kind === 'tool') {
        if (!isPlaceholderVoid(node)) {
          // 最新组“不折叠”只表示成员行可见；工具详情仍默认折叠。
          // 运行中状态由 ToolCallRow 的状态点与扫光表达，避免 spawn_sub_agent
          // 等长参数自动撑开整张卡片。
          const expanded = registry.isExpanded(node.key, false);
          rows.push(
            <ToolCallTreeBranch
              key={node.key}
              node={node}
              expanded={expanded}
              onExpandedChange={() => registry.toggle(node.key, expanded)}
            />,
          );
        }
        i += 1;
        continue;
      }
      if (node.kind === 'delegation') {
        // 连续委派聚合为一行（同组内多个子代理一次浏览）。
        let j = i;
        const delegationNodes: DelegationNode[] = [];
        while (j < nodes.length && nodes[j].kind === 'delegation') {
          delegationNodes.push(nodes[j] as DelegationNode);
          j += 1;
        }
        const firstKey = delegationNodes[0].key;
        const expanded = registry.isExpanded(firstKey, false);
        rows.push(
          <DelegationRow
            key={firstKey}
            nodes={delegationNodes}
            onOpenInspector={onOpenInspector}
            expanded={expanded}
            onExpandedChange={() => registry.toggle(firstKey, expanded)}
          />,
        );
        i = j;
        continue;
      }
      // retry：不渲染行（摘要计数承载），跳过。
      i += 1;
    }
    return rows;
  };

  return (
    <div
      className={cx(styles.activityGroup)}
      data-testid="activity-group"
      data-group-key={block.key}
      data-expanded={groupExpanded}
      data-latest={isLatestGroup}
      data-tail={isTailGroup}
    >
      <ExecutionDisclosureRow
        leading={
          <StateDot state={block.hasRunningNode ? 'ongoing' : 'done'} size={10} />
        }
        testId="activity-group-header"
        ariaLabel={`行为轨迹：${label}`}
        className={cx(block.hasRunningNode && styles.rowSweep)}
        expanded={groupExpanded}
        onExpandedChange={() => registry.toggle(block.key, groupExpanded)}
        expandedContent={
          groupExpanded ? (
            <div className={styles.activityGroupBody}>
              {hiddenNodeCount > 0 && (
                <button
                  type="button"
                  className={styles.trajectoryWindowButton}
                  data-testid="activity-group-reveal-earlier"
                  onClick={() =>
                    setVisibleNodeLimit((current) =>
                      Math.min(
                        block.nodes.length,
                        current + ACTIVITY_NODE_REVEAL_BATCH,
                      ),
                    )
                  }
                >
                  加载较早 {Math.min(hiddenNodeCount, ACTIVITY_NODE_REVEAL_BATCH)} 项
                  （尚有 {hiddenNodeCount} 项）
                </button>
              )}
              {renderNodes(visibleNodes)}
            </div>
          ) : null
        }
      >
        <span className={styles.reasoningTitle} data-testid="activity-group-label">
          {label}
        </span>
        {durationText && (
          <span className={styles.reasoningChip}>{durationText}</span>
        )}
      </ExecutionDisclosureRow>
    </div>
  );
};

/**
 * canonical projector 会在新事件到达时生成新的节点对象。按可见语义比较能让
 * 已封闭的历史组真正保持静止；仅尾组/状态变化组进入 React render。
 */
const sameActivityNode = (left: ActivityNode, right: ActivityNode): boolean => {
  if (
    left.kind !== right.kind ||
    left.key !== right.key ||
    left.sourceEventIds.length !== right.sourceEventIds.length ||
    left.sourceEventIds[left.sourceEventIds.length - 1] !==
      right.sourceEventIds[right.sourceEventIds.length - 1]
  ) {
    return false;
  }
  if (left.kind === 'reasoning' && right.kind === 'reasoning') {
    return (
      left.text === right.text &&
      left.blocks.length === right.blocks.length &&
      left.lastOccurredAt === right.lastOccurredAt
    );
  }
  if (left.kind === 'tool' && right.kind === 'tool') {
    return (
      left.state === right.state &&
      left.name === right.name &&
      left.arguments === right.arguments &&
      left.output === right.output &&
      left.error === right.error &&
      left.exitCode === right.exitCode &&
      left.durationMs === right.durationMs &&
      left.children.length === right.children.length &&
      left.children.every((child, index) =>
        sameActivityNode(child, right.children[index]),
      )
    );
  }
  if (left.kind === 'delegation' && right.kind === 'delegation') {
    return (
      left.state === right.state &&
      left.taskSummary === right.taskSummary &&
      left.replySummary === right.replySummary &&
      left.error === right.error
    );
  }
  return (
    left.kind === 'retry' &&
    right.kind === 'retry' &&
    left.attempt === right.attempt &&
    left.maxRetries === right.maxRetries &&
    left.reasonSummary === right.reasonSummary
  );
};

export default React.memo(
  ActivityGroup,
  (previous, next) =>
    previous.block.key === next.block.key &&
    previous.block.hasRunningNode === next.block.hasRunningNode &&
    previous.block.nodes.length === next.block.nodes.length &&
    previous.block.nodes.every((node, index) =>
      sameActivityNode(node, next.block.nodes[index]),
    ) &&
    previous.isLatestGroup === next.isLatestGroup &&
    previous.isTailGroup === next.isTailGroup &&
    previous.isRunActive === next.isRunActive &&
    previous.registry === next.registry &&
    previous.onOpenInspector === next.onOpenInspector,
);
