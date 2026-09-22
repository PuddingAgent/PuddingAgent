// ── terminalBodyAlignment：终态正文与权威全文的对齐（2026-09-23）────────────
//
// 背景（父级实测，见 Docs/Features/Chat前端架构设计方案-2026-09-22.md §12 第 4 项）：
//   投影正文短于权威全文时，界面只渲染投影那份 —— 因为
//     (a) AgentMessageBubble 只要投影存在任一非空 message 节点，就关闭承载完整
//         answerMarkdown 的兜底气泡（避免"轨迹块 + 正文块"两段式 UI）；
//     (b) executionFlowProjector 明确不用 message.completed.reply 覆盖段文本。
//   实测：投影 800 / 全文 2355 ⇒ 界面渲染 800（丢 1555 字符，且无任何兜底）。
//
// 本模块只做一件**可判定**的事，且**只在 turn 终态**：
//   若「投影正文段拼接」是「权威全文」的**前缀**且更短 ⇒ 只把缺的尾段补到
//   **最后一个**正文段上；其余情况**一律不动**（不猜、不重排、不新建节点）。
//   ⇒ 仍然只有一个正文区域、正文仍只渲染一次；**不打开第二个正文源**。
//
// 为什么不用"重开兜底气泡"这种更省事的做法：那会同时渲染投影正文与整块
// answerMarkdown，重新制造两段式重复正文（正是 2026-08-25 重构要消灭的形态）。
//
// 纯度约束：无 IO / 无时间源 / 无 React / 无副作用；不改入参对象（仅在需要时浅拷贝）。
import type { ExecutionFlowNode, MessageNode } from './executionFlowProjector';

/** 只依赖 nodes 的投影结构约束（避免耦合具体投影类型）。 */
export interface ProjectionLike {
  nodes: ExecutionFlowNode[];
}

export type TerminalBodyAlignmentOutcome =
  /** 非终态（运行中/流式中）：不介入，避免与流式增量竞争。 */
  | 'not-terminal'
  /** 没有投影：正文走兜底气泡路径，本模块不参与。 */
  | 'no-projection'
  /** 权威全文为空/全空白：无可对齐的权威，保持原样。 */
  | 'no-authoritative-text'
  /** 投影里没有正文节点：同上（兜底气泡路径）。 */
  | 'no-text-node'
  /** 投影正文段拼接已等于权威全文：无需处理。 */
  | 'already-complete'
  /** 投影是权威全文的前缀且更短：已把缺的尾段补到最后一个正文段。 */
  | 'appended-tail'
  /** 投影拼接不是权威全文的前缀（或更长）：**不猜**，保持原样。 */
  | 'diverged';

export interface TerminalBodyAlignmentResult<P extends ProjectionLike> {
  projection: P | undefined;
  outcome: TerminalBodyAlignmentOutcome;
  /** 本次补入的字符数（未补则为 0）。 */
  appendedChars: number;
}

/** 取投影中所有非空正文节点的下标（按投影顺序，即 canonical sequence 顺序）。 */
const textNodeIndexes = (nodes: readonly ExecutionFlowNode[]): number[] =>
  nodes.reduce<number[]>((acc, node, index) => {
    if (node.kind === 'message' && node.text.length > 0) acc.push(index);
    return acc;
  }, []);

export function alignTerminalBodyText<P extends ProjectionLike>(
  projection: P | undefined,
  authoritativeText: string,
  options: { terminal: boolean },
): TerminalBodyAlignmentResult<P> {
  const unchanged = (
    outcome: TerminalBodyAlignmentOutcome,
  ): TerminalBodyAlignmentResult<P> => ({
    projection,
    outcome,
    appendedChars: 0,
  });

  if (!options.terminal) return unchanged('not-terminal');
  if (!projection) return unchanged('no-projection');

  const authoritative = authoritativeText ?? '';
  if (authoritative.trim().length === 0) {
    return unchanged('no-authoritative-text');
  }

  const indexes = textNodeIndexes(projection.nodes);
  if (indexes.length === 0) return unchanged('no-text-node');

  const concatenated = indexes
    .map((index) => (projection.nodes[index] as MessageNode).text)
    .join('');

  if (concatenated === authoritative) return unchanged('already-complete');
  // 前缀判定必须是"投影拼接是权威全文的前缀"——只要不是，一律不动。
  // 注意：本判定对空白差异是 fail-closed 的（不一致即 diverged），不猜。
  if (!authoritative.startsWith(concatenated)) return unchanged('diverged');

  const appended = authoritative.slice(concatenated.length);
  if (appended.length === 0) return unchanged('already-complete');

  const lastIndex = indexes[indexes.length - 1];
  const nodes = projection.nodes.slice();
  const lastNode = nodes[lastIndex] as MessageNode;
  nodes[lastIndex] = { ...lastNode, text: lastNode.text + appended };

  return {
    projection: { ...projection, nodes },
    outcome: 'appended-tail',
    appendedChars: appended.length,
  };
}
