// ── ToolCallTree：工具调用递归树（CU-07）────────────────────────────────────
// 基于 ToolNode.children（父子嵌套调用，投影器 buildToolTree 已按
// parentToolCallId 建树）：父行 + 缩进子列表递归渲染。
//  - 占位过滤：placeholder=true 且无真实结果（output/error/exitCode）时隐藏
//    （result 先于 started 到达的占位调用，started 补全前不呈现空壳）；
//  - 多根：nodes 数组顶层即多个独立根（同一 turn 内多次顶层工具调用）。
// 消费点适配：buildToolTreeFromProcessItems 把 TimelineItem[]（旧消费点输入）
// 转换为 ToolNode[]——保留旧配对算法语义（toolCallId 精确配对、乱序正确、
// 缺失 id 不猜测）+ 建树逻辑（parentToolCallId 明确存在时成树）。
import React from 'react';
import type { ToolNode } from '../../projections/executionFlowProjector';
import type { TimelineItem } from '../../types';
import { useToolCallStyles } from '../../styles/toolcall.styles';
import { sanitizeProcessText } from '../processPreview';
import { ToolCallRow } from './ToolCallRow';

/** 占位节点判定：placeholder=true 且无任何真实结果 → 隐藏（不渲染空壳）。 */
const isPlaceholderVoid = (node: ToolNode): boolean =>
  Boolean(
    node.placeholder &&
      !node.output &&
      !node.error &&
      node.exitCode === undefined,
  );

export interface ToolCallTreeProps {
  /** 有序工具根节点（每个根可能带 children 子树）。 */
  nodes: ToolNode[];
}

/** 递归分支：单节点行 +（有 children 时）缩进子列表。 */
const ToolCallTreeBranch: React.FC<{ node: ToolNode }> = ({ node }) => {
  const { styles } = useToolCallStyles();
  const visibleChildren = node.children.filter(
    (child) => !isPlaceholderVoid(child),
  );
  return (
    <div className={styles.treeBranch}>
      <ToolCallRow node={node} />
      {visibleChildren.length > 0 && (
        <div className={styles.treeChildren} data-testid="toolcall-tree-children">
          {visibleChildren.map((child) => (
            <ToolCallTreeBranch key={child.key} node={child} />
          ))}
        </div>
      )}
    </div>
  );
};

/** 工具调用树：过滤占位后无可见根 → null（不占用布局）。 */
export const ToolCallTree: React.FC<ToolCallTreeProps> = ({ nodes }) => {
  const { styles } = useToolCallStyles();
  const roots = nodes.filter((node) => !isPlaceholderVoid(node));
  if (roots.length === 0) return null;
  return (
    <div className={styles.list} data-testid="toolcall-list">
      {roots.map((node) => (
        <ToolCallTreeBranch key={node.key} node={node} />
      ))}
    </div>
  );
};

// ── 消费点适配：TimelineItem[] → ToolNode[]（迁移旧 buildToolCallRows + buildToolTree）──

interface PairResult {
  call: TimelineItem;
  result?: TimelineItem;
}

/**
 * 配对 tool_call → tool_result（按 canonical toolCallId 精确配对，乱序正确；
 * 缺失 id 与不同 id 均不配对；孤儿 tool_result 不进行）。
 * 返回按 call 输入顺序排列的配对结果。
 */
export const pairToolCallItems = (items: TimelineItem[]): PairResult[] => {
  const calls: TimelineItem[] = [];
  const results: TimelineItem[] = [];
  for (const item of items) {
    if (item.type === 'tool_call') calls.push(item);
    else if (item.type === 'tool_result') results.push(item);
  }
  const used = new Set<string>();
  return calls.map((call) => {
    if (!call.toolCallId) return { call };
    const result = results.find(
      (candidate) =>
        !used.has(candidate.id) && candidate.toolCallId === call.toolCallId,
    );
    if (result) used.add(result.id);
    return { call, result };
  });
};

/** 行状态派生（对齐旧 resolveStatus 语义）：未配对=running；显式非零 exitCode 或
 *  status 文案含 error/fail/cancel → failed；否则 completed。 */
const deriveToolState = (
  result: TimelineItem | undefined,
): ToolNode['state'] => {
  if (!result) return 'running';
  const s = sanitizeProcessText(result.status).toLowerCase();
  if (
    s.includes('error') ||
    s.includes('fail') ||
    s.includes('cancel') ||
    (typeof result.exitCode === 'number' && result.exitCode !== 0)
  ) {
    return 'failed';
  }
  return 'completed';
};

/**
 * 从 processItems（TimelineItem[]）构建 ToolNode 列表（未建树，children=[]）。
 * 仅 tool_call 条目生成节点；thinking/subagent_* 不进入（由其他组件呈现）。
 */
export const buildToolNodesFromProcessItems = (
  items: TimelineItem[],
): ToolNode[] => {
  const pairs = pairToolCallItems(items);
  return pairs.map(({ call, result }, index) => {
    const state = deriveToolState(result);
    return {
      kind: 'tool',
      key: `tool:${call.toolCallId ?? call.id}`,
      firstEventId: call.eventId,
      sequence: index,
      occurredAt: undefined,
      sourceEventIds: [call.eventId].filter(
        (id): id is string => typeof id === 'string' && id.trim().length > 0,
      ),
      toolCallId: call.toolCallId ?? call.id,
      parentToolCallId: call.parentToolCallId,
      state,
      placeholder: Boolean(call.placeholder && !result),
      name: call.name,
      arguments: call.arguments,
      output: result?.output,
      error: result?.message,
      exitCode: result?.exitCode,
      durationMs: result?.durationMs ?? call.durationMs,
      presentation: call.presentation,
      children: [],
    };
  });
};

/**
 * 构建调用树：parentToolCallId 明确存在且父节点存在时挂为 children；
 * 否则作为根。子节点按输入顺序稳定排序（sequence 升序）。
 * 返回根节点列表（顶层调用按输入顺序）。
 */
export const buildToolTreeFromProcessItems = (
  items: TimelineItem[],
): ToolNode[] => {
  const nodes = buildToolNodesFromProcessItems(items);
  const byCallId = new Map<string, ToolNode>();
  for (const node of nodes) byCallId.set(node.toolCallId, node);

  const roots: ToolNode[] = [];
  const childCount = new Map<ToolNode, number>();
  for (const node of nodes) {
    const parentId = node.parentToolCallId;
    const parent = parentId ? byCallId.get(parentId) : undefined;
    if (parentId && parent) {
      parent.children.push(node);
      childCount.set(parent, (childCount.get(parent) ?? 0) + 1);
    } else {
      roots.push(node);
    }
  }
  for (const node of nodes) {
    if ((childCount.get(node) ?? 0) > 1) {
      node.children.sort((a, b) => a.sequence - b.sequence);
    }
  }
  return roots;
};

export default React.memo(ToolCallTree);
