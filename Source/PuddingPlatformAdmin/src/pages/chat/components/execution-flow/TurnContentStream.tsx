// ── TurnContentStream：Turn 内容块流（AgentTurnCard 重构 2026-08-25）───────
// AgentTurnCard 的唯一内容渲染器：按 canonical sequence 把投影 nodes 解释为
// 「正文段（永久可见）⇄ 行为组（可折叠）」交错块流。
//  - 路径 B（canonical）：projection.nodes → buildTurnContentBlocks；
//  - 路径 A 回退（无投影的旧 turn / 逃生门）：processItems 适配为同构节点集
//    （无正文段），两条路径共享同一块结构与同一渲染组件。
// 退役：ExecutionFlowTimeline 的 messageSegments / deriveTrailingMessageNode /
// buildEntriesFromProcessItems 双区域逻辑——正文只在本流中渲染一次，卡片底部
// 不再存在第二个 answer bubble。
// 流式策略：只有「最后一个正文块 && terminal='none' && run 活跃」走打字机；
// 新行为组到来冻结当前段（不卸载不重排），新正文段到达只 mount 新段。
import React from 'react';
import type {
  ActivityNode,
  TurnContentBlock,
} from '../../projections/turnContentBlocks';
import { buildTurnContentBlocks } from '../../projections/turnContentBlocks';
import type {
  ExecutionFlowProjection,
  ReasoningNode,
} from '../../projections/executionFlowProjector';
import type { TimelineItem } from '../../types';
import type { TimelineTurnStats } from '../../projections/turnContentBlocks';
import { sanitizeProcessText } from '../processPreview';
import { useExecutionFlowStyles } from '../../styles/execution-flow.styles';
import {
  buildDelegationNodesFromProcessItems,
} from './DelegationRow';
import ActivityGroup from './ActivityGroup';
import TextSegmentView from './TextSegmentView';
import {
  buildToolTreeFromProcessItems,
} from './ToolCallTree';
import { useDisclosureRegistry } from './useDisclosureRegistry';

const INITIAL_VISIBLE_TURN_BLOCKS = 40;
const TURN_BLOCK_REVEAL_BATCH = 40;

/** timestamp（毫秒）→ ISO；非法/缺失 → undefined（不伪造时间源）。 */
const isoFromTimestamp = (timestamp?: number): string | undefined =>
  typeof timestamp === 'number' && Number.isFinite(timestamp) && timestamp > 0
    ? new Date(timestamp).toISOString()
    : undefined;

/**
 * 路径 A 适配：processItems（TimelineItem[]，按时间序）→ 行为节点集（无正文
 * 段）。连续 thinking 合并为 ReasoningNode（blocks 直接当行用）；工具经
 * buildToolTreeFromProcessItems 配对建树后锚定在 tool_call 位置；委派按
 * subAgentId 聚合后锚定在首次 spawn 位置。纯函数。
 */
export function buildActivityNodesFromProcessItems(
  items: readonly TimelineItem[],
): ActivityNode[] {
  const toolRoots = buildToolTreeFromProcessItems(items);
  const rootByCallId = new Map(toolRoots.map((root) => [root.toolCallId, root]));
  const delegationById = new Map(
    buildDelegationNodesFromProcessItems(items).map((node) => [
      node.subAgentId,
      node,
    ]),
  );
  const spawnedDelegationIds = new Set<string>();

  const nodes: ActivityNode[] = [];
  let openReasoning: ReasoningNode | null = null;
  const flushReasoning = () => {
    openReasoning = null;
  };
  let sequence = 0;

  for (const item of items) {
    if (item.type === 'thinking') {
      const text = sanitizeProcessText(item.text, { compact: false });
      if (!text.trim()) continue;
      if (!openReasoning) {
        openReasoning = {
          kind: 'reasoning',
          key: `reasoning:${item.id}`,
          firstEventId: item.eventId,
          sequence: sequence++,
          occurredAt: isoFromTimestamp(item.timestamp),
          sourceEventIds: item.eventId ? [item.eventId] : [],
          text: '',
          blocks: [],
        };
        nodes.push(openReasoning);
      }
      openReasoning.blocks.push({
        id: item.id,
        text,
        sourceIds: item.eventId ? [item.eventId] : [],
      });
      openReasoning.text += text;
      openReasoning.lastOccurredAt = isoFromTimestamp(item.timestamp);
      continue;
    }
    flushReasoning();
    if (item.type === 'tool_call' && item.toolCallId) {
      const root = rootByCallId.get(item.toolCallId);
      if (root) {
        rootByCallId.delete(item.toolCallId);
        nodes.push(root);
      }
      continue;
    }
    if (item.type === 'subagent_spawned') {
      const subAgentId = item.name?.trim() || item.id;
      const node = delegationById.get(subAgentId);
      if (!node || spawnedDelegationIds.has(subAgentId)) continue;
      spawnedDelegationIds.add(subAgentId);
      nodes.push(node);
    }
  }
  flushReasoning();
  return nodes;
}

/** 工具树（含子调用）计数；占位空壳不计。 */
const countToolTree = (nodes: readonly { children: unknown[] }[]): number => {
  let count = 0;
  for (const node of nodes) {
    count += 1 + countToolTree(node.children as { children: unknown[] }[]);
  }
  return count;
};

/** 路径 A 回退统计（自退役的 ExecutionFlowTimeline 迁入）。 */
export const deriveStatsFromProcessItems = (
  items: readonly TimelineItem[],
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
  return {
    reasoningSegments,
    toolCount: countToolTree(buildToolTreeFromProcessItems(items)),
  };
};

export interface TurnContentStreamProps {
  /** 路径 B：canonical 投影（提供时优先生效）。 */
  projection?: ExecutionFlowProjection;
  /** 路径 A 回退：processItems（TimelineItem[]，按时间序；无投影时使用）。 */
  processItems?: readonly TimelineItem[];
  /** turn 是否仍在运行（尾部组/尾部正文段的流式与当前段判定）。 */
  isRunActive?: boolean;
  workspaceId?: string;
  /** 正文段右键（复用 answer bubble 的上下文菜单）。 */
  onAnswerContextMenu?: (e: React.MouseEvent) => void;
  /** 委派展开态「打开检查器」入口。 */
  onOpenInspector?: (runId: string) => void;
}

/** 内容块流：无可见块 → null（不占用布局）。 */
export const TurnContentStream: React.FC<TurnContentStreamProps> = ({
  projection,
  processItems,
  isRunActive = false,
  workspaceId,
  onAnswerContextMenu,
  onOpenInspector,
}) => {
  const { styles } = useExecutionFlowStyles();
  const registry = useDisclosureRegistry();
  const [visibleBlockLimit, setVisibleBlockLimit] = React.useState(
    INITIAL_VISIBLE_TURN_BLOCKS,
  );

  const blocks = React.useMemo<TurnContentBlock[]>(() => {
    const nodes = projection
      ? projection.nodes
      : buildActivityNodesFromProcessItems(processItems ?? []);
    return buildTurnContentBlocks(nodes);
  }, [projection, processItems]);

  // 尾部开放正文段（run 活跃 && 未落终态）→ 打字机；其余正文段静态。
  let tailTextKey: string | undefined;
  for (let i = blocks.length - 1; i >= 0; i--) {
    const block = blocks[i];
    if (block.kind !== 'text') continue;
    tailTextKey = block.key;
    break;
  }
  let streamingTextKey: string | undefined;
  if (isRunActive && tailTextKey !== undefined) {
    const tailBlock = blocks.find(
      (block) => block.kind === 'text' && block.key === tailTextKey,
    );
    if (tailBlock?.kind === 'text' && tailBlock.node.terminal === 'none') {
      streamingTextKey = tailTextKey;
    }
  }

  if (blocks.length === 0) return null;

  const hiddenBlockCount = Math.max(0, blocks.length - visibleBlockLimit);
  const visibleBlocks =
    hiddenBlockCount > 0 ? blocks.slice(hiddenBlockCount) : blocks;

  const latestActivityGroupKey = [...blocks]
    .reverse()
    .find((block) => block.kind === 'activity-group')?.key;

  return (
    <div
      className={styles.turnContentStream}
      data-testid="turn-content-stream"
    >
      {hiddenBlockCount > 0 && (
        <button
          type="button"
          className={styles.trajectoryWindowButton}
          data-testid="turn-content-reveal-earlier"
          onClick={() =>
            setVisibleBlockLimit((current) =>
              Math.min(blocks.length, current + TURN_BLOCK_REVEAL_BATCH),
            )
          }
        >
          加载较早 {Math.min(hiddenBlockCount, TURN_BLOCK_REVEAL_BATCH)} 个内容块
          （尚有 {hiddenBlockCount} 个）
        </button>
      )}
      {visibleBlocks.map((block, index) =>
        block.kind === 'text' ? (
          <TextSegmentView
            key={block.key}
            node={block.node}
            streaming={block.key === streamingTextKey}
            workspaceId={workspaceId}
            onContextMenu={onAnswerContextMenu}
          />
        ) : (
          <ActivityGroup
            key={block.key}
            block={block}
            isLatestGroup={block.key === latestActivityGroupKey}
            isTailGroup={hiddenBlockCount + index === blocks.length - 1}
            isRunActive={isRunActive}
            registry={registry}
            onOpenInspector={onOpenInspector}
          />
        ),
      )}
    </div>
  );
};

export default React.memo(TurnContentStream);
