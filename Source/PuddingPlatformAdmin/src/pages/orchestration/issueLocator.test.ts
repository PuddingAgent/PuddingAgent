import {
  buildIssueFocusPlan,
  computeEdgeMidpoint,
  computeNodeCenter,
  resolveIssueTarget,
} from './issueLocator';
import type { OrchestrationGraphDefinition } from './types';

const issue = (
  overrides: Partial<{
    severity: 'error' | 'warning' | 'info';
    elementType?: string;
    elementId?: string;
    portId?: string;
  }> = {},
) => ({
  code: 'graph.test',
  message: 'Test issue',
  severity: 'error' as const,
  ...overrides,
});

const definition: OrchestrationGraphDefinition = {
  schemaVersion: 'pudding.agent-orchestration/v2',
  graphId: 'graph-1',
  revisionId: 'graph-1/r001',
  revision: 1,
  workspaceId: 'default',
  rootSessionId: 'session-1',
  createdByAgentId: 'agent-1',
  objective: 'Locator',
  requiresExplicitActivation: true,
  maxConcurrency: 1,
  nodes: [
    {
      nodeId: 'a',
      kind: 'tool',
      title: 'A',
      objective: 'A',
      component: { componentType: 'a', version: '1' },
      executor: { kind: 'tool', toolId: 'a' },
      expectedOutputContract: 'pudding.text',
      configuration: {},
      permissionMode: 'readOnly',
      failureBehavior: 'failRun',
      maxAttempts: 1,
      metadata: {},
    },
    {
      nodeId: 'b',
      kind: 'tool',
      title: 'B',
      objective: 'B',
      component: { componentType: 'b', version: '1' },
      executor: { kind: 'tool', toolId: 'b' },
      expectedOutputContract: 'pudding.text',
      configuration: {},
      permissionMode: 'readOnly',
      failureBehavior: 'failRun',
      maxAttempts: 1,
      metadata: {},
    },
  ],
  edges: [
    {
      edgeId: 'edge-1',
      fromNodeId: 'a',
      toNodeId: 'b',
      kind: 'data',
      condition: 'onSuccess',
      bindings: [
        {
          sourcePortId: 'out',
          sourcePath: '$',
          targetPortId: 'in',
          aggregation: 'replace',
        },
      ],
    },
  ],
  metadata: {},
  createdAtUtc: '2026-08-13T00:00:00Z',
};

const positions = {
  a: { x: 0, y: 0, width: 200, height: 80 },
  b: { x: 400, y: 100, width: 200, height: 80 },
};

describe('S2-B5-3a issue click-to-locate', () => {
  it('resolves edge, node and port targets from backend locator fields', () => {
    expect(resolveIssueTarget(issue({ elementType: 'edge', elementId: 'e1' }))).toEqual(
      { kind: 'edge', edgeId: 'e1' },
    );
    expect(
      resolveIssueTarget(issue({ elementType: 'node', elementId: 'n1' })),
    ).toEqual({ kind: 'node', nodeId: 'n1' });
    expect(
      resolveIssueTarget(
        issue({ elementType: 'node', elementId: 'n1', portId: 'p1' }),
      ),
    ).toEqual({ kind: 'port', nodeId: 'n1', portId: 'p1' });
  });

  it('returns undefined for issues without a resolvable element id or kind', () => {
    expect(resolveIssueTarget(issue())).toBeUndefined();
    expect(resolveIssueTarget(issue({ elementId: '' }))).toBeUndefined();
    expect(
      resolveIssueTarget(issue({ elementType: 'trigger', elementId: 't1' })),
    ).toBeUndefined();
  });

  it('computes node centers with explicit and default dimensions', () => {
    expect(computeNodeCenter(positions, 'a')).toEqual({ x: 100, y: 40 });
    expect(computeNodeCenter({ a: { x: 10, y: 20 } }, 'a')).toEqual({
      x: 10 + 230 / 2,
      y: 20 + 76 / 2,
    });
    expect(computeNodeCenter(positions, 'missing')).toBeUndefined();
  });

  it('computes the edge midpoint from its endpoint node centers', () => {
    expect(computeEdgeMidpoint(definition, 'edge-1', positions)).toEqual({
      x: (100 + 500) / 2,
      y: (40 + 140) / 2,
    });
    expect(
      computeEdgeMidpoint(definition, 'edge-missing', positions),
    ).toBeUndefined();
    expect(computeEdgeMidpoint(definition, 'edge-1', {})).toBeUndefined();
  });

  it('builds a full focus plan for edge, node and port issues', () => {
    const edgePlan = buildIssueFocusPlan(
      issue({ elementType: 'edge', elementId: 'edge-1' }),
      definition,
      positions,
    );
    expect(edgePlan).toEqual({
      target: { kind: 'edge', edgeId: 'edge-1' },
      center: { x: 300, y: 90 },
    });

    const nodePlan = buildIssueFocusPlan(
      issue({ elementType: 'node', elementId: 'a' }),
      definition,
      positions,
    );
    expect(nodePlan?.target).toEqual({ kind: 'node', nodeId: 'a' });
    expect(nodePlan?.center).toEqual({ x: 100, y: 40 });

    const portPlan = buildIssueFocusPlan(
      issue({ elementType: 'node', elementId: 'b', portId: 'in' }),
      definition,
      positions,
    );
    expect(portPlan?.target).toEqual({
      kind: 'port',
      nodeId: 'b',
      portId: 'in',
    });
    expect(portPlan?.center).toEqual({ x: 500, y: 140 });
  });

  it('returns undefined when the target cannot be placed', () => {
    expect(
      buildIssueFocusPlan(
        issue({ elementType: 'node', elementId: 'missing' }),
        definition,
        positions,
      ),
    ).toBeUndefined();
    expect(
      buildIssueFocusPlan(
        issue({ elementType: 'trigger', elementId: 't1' }),
        definition,
        positions,
      ),
    ).toBeUndefined();
  });
});
