import { buildOrchestrationFlowModel } from './graphViewModel';
import type { OrchestrationGraphDefinition, OrchestrationRunSnapshot } from './types';

const definition = {
  schemaVersion: 'pudding.agent-orchestration/v2',
  graphId: 'graph-1',
  revisionId: 'graph-1/rev-1',
  revision: 1,
  workspaceId: 'default',
  rootSessionId: 'session-1',
  createdByAgentId: 'agent-1',
  objective: 'test',
  requiresExplicitActivation: true,
  maxConcurrency: 2,
  metadata: {},
  createdAtUtc: '2026-08-09T00:00:00Z',
  nodes: ['research', 'proposal', 'review'].map((nodeId) => ({
    nodeId,
    kind: 'subAgent' as const,
    title: nodeId,
    objective: nodeId,
    component: { componentType: 'pudding.agent.subagent', version: '1' },
    expectedOutputContract: 'pudding.text',
    configuration: {},
    permissionMode: 'readOnly' as const,
    failureBehavior: 'failRun' as const,
    maxAttempts: 1,
    metadata: {},
  })),
  edges: [
    {
      edgeId: 'e1',
      fromNodeId: 'research',
      toNodeId: 'proposal',
      kind: 'data' as const,
      condition: 'onSuccess' as const,
      bindings: [],
    },
    {
      edgeId: 'e2',
      fromNodeId: 'proposal',
      toNodeId: 'review',
      kind: 'control' as const,
      condition: 'onCompletion' as const,
      bindings: [],
    },
  ],
} satisfies OrchestrationGraphDefinition;

const run = {
  runId: 'run-1',
  graphId: 'graph-1',
  revisionId: 'graph-1/rev-1',
  workspaceId: 'default',
  rootSessionId: 'session-1',
  requestedByAgentId: 'agent-1',
  status: 'active',
  version: 2,
  headSequence: 4,
  maxConcurrency: 2,
  createdAtUtc: '2026-08-09T00:00:00Z',
  updatedAtUtc: '2026-08-09T00:01:00Z',
  nodes: definition.nodes.map((node, index) => ({
    nodeId: node.nodeId,
    kind: node.kind,
    status: index === 0 ? ('completed' as const) : index === 1 ? ('running' as const) : ('pending' as const),
    attempt: index === 1 ? 1 : 0,
    maxAttempts: 1,
    fencingToken: 0,
    updatedAtUtc: '2026-08-09T00:01:00Z',
  })),
} satisfies OrchestrationRunSnapshot;

describe('orchestration graph view model', () => {
  it('lays a DAG out from left to right and projects run status', () => {
    const model = buildOrchestrationFlowModel(definition, run);
    expect(model.nodes.map((node) => node.position.x)).toEqual([0, 310, 620]);
    expect(model.nodes[1].data.status).toBe('running');
    expect(model.nodes[1].style).toEqual(expect.objectContaining({ border: expect.stringContaining('#13c2c2') }));
  });

  it('visually distinguishes data edges from control edges', () => {
    const model = buildOrchestrationFlowModel(definition, run);
    expect(model.edges[0].style).toEqual(expect.objectContaining({ strokeDasharray: '6 4' }));
    expect(model.edges[1].style?.strokeDasharray).toBeUndefined();
  });

  it('uses editor layout coordinates without changing executable edges', () => {
    const model = buildOrchestrationFlowModel(definition, run, {
      graphId: definition.graphId,
      baseRevisionId: definition.revisionId,
      layoutRevision: 3,
      viewport: { x: 20, y: 30, zoom: 1.2 },
      nodes: [
        { nodeId: 'proposal', x: 777, y: 333, width: 280, collapsed: false },
      ],
    });
    const proposal = model.nodes.find((node) => node.id === 'proposal');
    expect(proposal?.position).toEqual({ x: 777, y: 333 });
    expect(proposal?.style?.width).toBe(280);
    expect(model.edges.map((edge) => edge.id)).toEqual(['e1', 'e2']);
  });
});
