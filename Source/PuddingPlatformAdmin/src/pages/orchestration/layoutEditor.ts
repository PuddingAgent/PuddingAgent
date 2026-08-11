import type {
  OrchestrationGraphLayout,
  OrchestrationLayoutWriteRequest,
} from './types';

interface EditableLayoutNode {
  id: string;
  position: { x: number; y: number };
}

interface BuildOrchestrationLayoutWriteOptions {
  graphId: string;
  baseRevisionId: string;
  currentLayout?: OrchestrationGraphLayout;
  viewport: OrchestrationGraphLayout['viewport'];
  nodes: EditableLayoutNode[];
}

export interface OrchestrationLayoutConflict {
  message: string;
  currentLayoutRevision?: number;
}

export function buildOrchestrationLayoutWrite({
  graphId,
  baseRevisionId,
  currentLayout,
  viewport,
  nodes,
}: BuildOrchestrationLayoutWriteOptions): OrchestrationLayoutWriteRequest {
  const expectedCurrentLayoutRevision = currentLayout?.layoutRevision ?? 0;
  const storedNodes = new Map(
    currentLayout?.nodes.map((node) => [node.nodeId, node]) ?? [],
  );

  return {
    expectedCurrentLayoutRevision,
    layout: {
      graphId,
      baseRevisionId,
      layoutRevision: expectedCurrentLayoutRevision + 1,
      viewport,
      nodes: nodes.map((node) => {
        const storedNode = storedNodes.get(node.id);
        return {
          nodeId: node.id,
          x: node.position.x,
          y: node.position.y,
          ...(storedNode?.width !== undefined
            ? { width: storedNode.width }
            : {}),
          ...(storedNode?.height !== undefined
            ? { height: storedNode.height }
            : {}),
          ...(storedNode?.parentNodeId
            ? { parentNodeId: storedNode.parentNodeId }
            : {}),
          collapsed: storedNode?.collapsed ?? false,
        };
      }),
    },
  };
}

export function getOrchestrationLayoutConflict(
  error: unknown,
): OrchestrationLayoutConflict | undefined {
  const candidate = error as {
    response?: { status?: number; data?: unknown };
    data?: unknown;
  };
  if (candidate?.response?.status !== 409) return undefined;

  const data = (candidate.data ?? candidate.response.data) as
    | { message?: unknown; currentLayoutRevision?: unknown }
    | undefined;
  return {
    message:
      typeof data?.message === 'string'
        ? data.message
        : '布局已被其他编辑者更新。',
    ...(typeof data?.currentLayoutRevision === 'number'
      ? { currentLayoutRevision: data.currentLayoutRevision }
      : {}),
  };
}
