import type {
  ExecutionFlowNode,
  ExecutionFlowProjection,
  ToolNode,
} from '../projections/executionFlowProjector';

// 一个行为节点通常会展开成行壳、状态、图标、Tooltip 与详情触发器等多层 DOM。
// 用结构成本补足纯字符权重，避免已水合 canonical 行为链被 viewport 低估。
const EXECUTION_FLOW_NODE_RENDER_WEIGHT = 512;
const REASONING_BLOCK_RENDER_WEIGHT = 128;

const textWeight = (...values: Array<string | undefined>): number =>
  values.reduce((total, value) => total + (value?.length ?? 0), 0);

const toolRenderWeight = (node: ToolNode): number =>
  EXECUTION_FLOW_NODE_RENDER_WEIGHT +
  textWeight(node.name, node.arguments, node.output, node.error) +
  node.children.reduce((total, child) => total + toolRenderWeight(child), 0);

const nodeRenderWeight = (node: ExecutionFlowNode): number => {
  switch (node.kind) {
    case 'reasoning':
      return (
        EXECUTION_FLOW_NODE_RENDER_WEIGHT +
        node.text.length +
        node.blocks.length * REASONING_BLOCK_RENDER_WEIGHT
      );
    case 'message':
      return (
        EXECUTION_FLOW_NODE_RENDER_WEIGHT +
        textWeight(node.text, node.errorMessage)
      );
    case 'tool':
      return toolRenderWeight(node);
    case 'delegation':
      return (
        EXECUTION_FLOW_NODE_RENDER_WEIGHT +
        textWeight(
          node.template,
          node.model,
          node.taskSummary,
          node.replySummary,
          node.error,
        )
      );
    case 'retry':
      return EXECUTION_FLOW_NODE_RENDER_WEIGHT + node.reasonFull.length;
    case 'terminal':
      return (
        EXECUTION_FLOW_NODE_RENDER_WEIGHT +
        textWeight(node.reply, node.errorMessage, node.message)
      );
  }
};

export const getExecutionFlowRenderWeight = (
  projection: ExecutionFlowProjection | undefined,
): number => {
  if (!projection) return 0;
  const cached = executionFlowRenderWeightCache.get(projection);
  if (cached !== undefined) return cached;
  const weight = projection.nodes.reduce(
    (total, node) => total + nodeRenderWeight(node),
    0,
  );
  executionFlowRenderWeightCache.set(projection, weight);
  return weight;
};

// Projection 是不可变快照；以对象身份缓存深层工具树/推理块的结构权重。
// WeakMap 不延长历史 Projection 生命周期，会话切换后可由 GC 自然回收。
const executionFlowRenderWeightCache = new WeakMap<
  ExecutionFlowProjection,
  number
>();
