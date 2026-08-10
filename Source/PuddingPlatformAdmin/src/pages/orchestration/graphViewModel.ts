import { MarkerType, type Edge, type Node } from '@xyflow/react';
import type {
  OrchestrationGraphDefinition,
  OrchestrationGraphLayout,
  OrchestrationNodeRunSnapshot,
  OrchestrationNodeRunStatus,
  OrchestrationRunSnapshot,
} from './types';

export interface OrchestrationFlowNodeData extends Record<string, unknown> {
  label: string;
  title: string;
  kind: string;
  status: OrchestrationNodeRunStatus;
  attempt: number;
  maxAttempts: number;
}

export type OrchestrationFlowNode = Node<OrchestrationFlowNodeData, 'default'>;

const statusBorder: Record<OrchestrationNodeRunStatus, string> = {
  pending: '#94a3b8',
  ready: '#1677ff',
  claimed: '#722ed1',
  running: '#13c2c2',
  awaitingInput: '#faad14',
  completed: '#52c41a',
  failed: '#ff4d4f',
  skipped: '#bfbfbf',
  cancelled: '#8c8c8c',
};

function calculateLevels(definition: OrchestrationGraphDefinition): Map<string, number> {
  const nodeIds = definition.nodes.map((node) => node.nodeId);
  const level = new Map(nodeIds.map((nodeId) => [nodeId, 0]));
  const indegree = new Map(nodeIds.map((nodeId) => [nodeId, 0]));
  const outgoing = new Map(nodeIds.map((nodeId) => [nodeId, [] as string[]]));
  for (const edge of definition.edges) {
    if (!indegree.has(edge.toNodeId) || !outgoing.has(edge.fromNodeId)) continue;
    indegree.set(edge.toNodeId, (indegree.get(edge.toNodeId) ?? 0) + 1);
    outgoing.get(edge.fromNodeId)?.push(edge.toNodeId);
  }

  const queue = nodeIds.filter((nodeId) => indegree.get(nodeId) === 0);
  for (let index = 0; index < queue.length; index += 1) {
    const nodeId = queue[index];
    for (const targetId of outgoing.get(nodeId) ?? []) {
      level.set(targetId, Math.max(level.get(targetId) ?? 0, (level.get(nodeId) ?? 0) + 1));
      const nextIndegree = (indegree.get(targetId) ?? 1) - 1;
      indegree.set(targetId, nextIndegree);
      if (nextIndegree === 0) queue.push(targetId);
    }
  }
  return level;
}

export function buildOrchestrationFlowModel(
  definition: OrchestrationGraphDefinition,
  run: OrchestrationRunSnapshot,
  layout?: OrchestrationGraphLayout,
): { nodes: OrchestrationFlowNode[]; edges: Edge[] } {
  const levels = calculateLevels(definition);
  const statusByNode = new Map<string, OrchestrationNodeRunSnapshot>(
    run.nodes.map((node) => [node.nodeId, node]),
  );
  const rowsByLevel = new Map<number, number>();
  const layoutByNode = new Map(layout?.nodes.map((node) => [node.nodeId, node]) ?? []);

  const nodes: OrchestrationFlowNode[] = definition.nodes.map((node) => {
    const nodeRun = statusByNode.get(node.nodeId);
    const status = nodeRun?.status ?? 'pending';
    const column = levels.get(node.nodeId) ?? 0;
    const row = rowsByLevel.get(column) ?? 0;
    const savedLayout = layoutByNode.get(node.nodeId);
    rowsByLevel.set(column, row + 1);
    return {
      id: node.nodeId,
      type: 'default',
      position: savedLayout
        ? { x: savedLayout.x, y: savedLayout.y }
        : { x: column * 310, y: row * 150 },
      data: {
        label: `${node.title} · ${status}`,
        title: node.title,
        kind: node.kind,
        status,
        attempt: nodeRun?.attempt ?? 0,
        maxAttempts: nodeRun?.maxAttempts ?? node.maxAttempts,
      },
      style: {
        width: savedLayout?.width ?? 230,
        minHeight: savedLayout?.height ?? 76,
        border: `2px solid ${statusBorder[status]}`,
        borderRadius: 10,
        whiteSpace: 'normal',
        boxShadow: status === 'running' ? `0 0 0 4px ${statusBorder[status]}22` : undefined,
      },
    };
  });

  const edges: Edge[] = definition.edges.map((edge) => {
    const isData = edge.kind === 'data';
    return {
      id: edge.edgeId,
      source: edge.fromNodeId,
      target: edge.toNodeId,
      label: isData ? '数据' : edge.condition,
      type: 'smoothstep',
      markerEnd: { type: MarkerType.ArrowClosed },
      style: {
        stroke: isData ? '#722ed1' : '#1677ff',
        strokeDasharray: isData ? '6 4' : undefined,
        strokeWidth: 2,
      },
    };
  });

  return { nodes, edges };
}
