import type {
  OrchestrationGraphDefinition,
  OrchestrationValidationIssue,
} from './types';

/**
 * S2-B5-3a: click-to-locate pure helpers (doc 85 §6.2:145 "server diagnostic
 * 点击可定位 node/edge/port"). The ReactFlow instance itself never touches this
 * module; index.tsx calls setCenter/setSelected* with the returned plan, so
 * every localization decision stays unit-testable without a DOM/canvas.
 */

/** Canvas element a validation issue points at. */
export type IssueTarget =
  | { kind: 'edge'; edgeId: string }
  | { kind: 'node'; nodeId: string }
  | { kind: 'port'; nodeId: string; portId: string };

/** Current canvas position of one flow node (position is always present). */
export interface NodeCanvasPosition {
  x: number;
  y: number;
  width?: number;
  height?: number;
}

/**
 * Resolves an issue to a stable canvas target. Backend
 * AgentOrchestrationGraphCompiler.cs emits ElementType="edge"/"node" with
 * ElementId and optional PortId (types.ts OrchestrationValidationIssue).
 * Issues without a resolvable element id return undefined (not clickable).
 */
export function resolveIssueTarget(
  issue: OrchestrationValidationIssue,
): IssueTarget | undefined {
  const elementId = issue.elementId?.trim();
  if (!elementId) return undefined;
  if (issue.elementType === 'edge') {
    return { kind: 'edge', edgeId: elementId };
  }
  if (issue.elementType === 'node') {
    const portId = issue.portId?.trim();
    return portId
      ? { kind: 'port', nodeId: elementId, portId }
      : { kind: 'node', nodeId: elementId };
  }
  return undefined;
}

/** Center of a flow node in canvas coordinates; defaults match graphViewModel. */
export function computeNodeCenter(
  positions: Record<string, NodeCanvasPosition>,
  nodeId: string,
): { x: number; y: number } | undefined {
  const position = positions[nodeId];
  if (!position) return undefined;
  return {
    x: position.x + (position.width ?? 230) / 2,
    y: position.y + (position.height ?? 76) / 2,
  };
}

/** Midpoint between the edge's two endpoint node centers. */
export function computeEdgeMidpoint(
  definition: OrchestrationGraphDefinition | undefined,
  edgeId: string,
  positions: Record<string, NodeCanvasPosition>,
): { x: number; y: number } | undefined {
  const edge = definition?.edges.find((item) => item.edgeId === edgeId);
  if (!edge) return undefined;
  const source = computeNodeCenter(positions, edge.fromNodeId);
  const target = computeNodeCenter(positions, edge.toNodeId);
  if (!source || !target) return undefined;
  return {
    x: (source.x + target.x) / 2,
    y: (source.y + target.y) / 2,
  };
}

/** Everything index.tsx needs to focus an issue in one call. */
export interface IssueFocusPlan {
  target: IssueTarget;
  center: { x: number; y: number };
}

/**
 * Builds the focus plan for an issue click:
 * - edge issues  -> edge target + viewport center at the edge midpoint;
 * - node/port    -> node target (port issues focus their owning node) + node
 *   center (port coordinates are not exposed by the flow model).
 * Returns undefined when the target cannot be resolved or placed.
 */
export function buildIssueFocusPlan(
  issue: OrchestrationValidationIssue,
  definition: OrchestrationGraphDefinition | undefined,
  positions: Record<string, NodeCanvasPosition>,
): IssueFocusPlan | undefined {
  const target = resolveIssueTarget(issue);
  if (!target) return undefined;
  const center =
    target.kind === 'edge'
      ? computeEdgeMidpoint(definition, target.edgeId, positions)
      : computeNodeCenter(positions, target.nodeId);
  if (!center) return undefined;
  return { target, center };
}
