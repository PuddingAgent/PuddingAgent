import { buildOrchestrationFlowModel, GRAPH_INPUTS_NODE_TYPE, GRAPH_INPUTS_VIRTUAL_NODE_ID, isGraphInputsVirtualNode } from './graphViewModel';
import type {
  OrchestrationCatalog,
  OrchestrationGraphDefinition,
  OrchestrationRunSnapshot,
} from './types';

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
    status:
      index === 0
        ? ('completed' as const)
        : index === 1
          ? ('running' as const)
          : ('pending' as const),
    attempt: index === 1 ? 1 : 0,
    maxAttempts: 1,
    fencingToken: 0,
    outputSummary: index === 0 ? 'Generated image.' : undefined,
    artifactReference: index === 0 ? 'vision-output-001' : undefined,
    outputs:
      index === 0
        ? {
            result: {
              dataType: 'pudding.content',
              contentType: 'text/markdown',
              inlineValue: '策划结果',
            },
          }
        : undefined,
    updatedAtUtc: '2026-08-09T00:01:00Z',
  })),
} satisfies OrchestrationRunSnapshot;

describe('orchestration graph view model', () => {
  it('lays a DAG out from left to right and projects run status', () => {
    const model = buildOrchestrationFlowModel(definition, run);
    expect(model.nodes.map((node) => node.position.x)).toEqual([0, 310, 620]);
    expect(model.nodes[1].data.status).toBe('running');
    expect(model.nodes[1].style).toEqual(
      expect.objectContaining({ border: expect.stringContaining('#13c2c2') }),
    );
    expect(model.nodes[0].data).toEqual(
      expect.objectContaining({
        componentType: 'pudding.agent.subagent',
        workspaceId: 'default',
        outputSummary: 'Generated image.',
        artifactReference: 'vision-output-001',
        outputs: expect.objectContaining({
          result: expect.objectContaining({ inlineValue: '策划结果' }),
        }),
      }),
    );
  });

  it('visually distinguishes data edges from control edges', () => {
    const model = buildOrchestrationFlowModel(definition, run);
    expect(model.edges[0].style).toEqual(
      expect.objectContaining({ strokeDasharray: '6 4' }),
    );
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

  it('renders a read-only virtual Graph Inputs node when inputs are declared', () => {
    const model = buildOrchestrationFlowModel(
      {
        ...definition,
        inputs: [
          {
            inputId: 'request',
            contract: {
              dataType: 'pudding.content',
              mediaTypes: ['text/plain'],
              cardinality: 'one',
              deliveries: ['inline'],
            },
            defaultValue: {
              dataType: 'pudding.content',
              contentType: 'text/plain',
              inlineValue: 'default prompt',
            },
            requiredAtActivation: true,
          },
        ],
      },
      run,
    );
    const virtual = model.nodes.find(
      (node) => node.id === GRAPH_INPUTS_VIRTUAL_NODE_ID,
    );
    expect(virtual).toBeDefined();
    expect(virtual?.type).toBe(GRAPH_INPUTS_NODE_TYPE);
    expect(virtual?.draggable).toBe(false);
    expect(virtual?.data.inputs).toHaveLength(1);
    expect(virtual?.data.inputs?.[0].inputId).toBe('request');
    expect(virtual?.data.inputs?.[0].defaultValue).toEqual({
      dataType: 'pudding.content',
      contentType: 'text/plain',
      inlineValue: 'default prompt',
    });
    expect(virtual?.data.inputPorts).toEqual([]);
    expect(virtual?.data.outputPorts).toEqual([]);
    expect(virtual?.position.x).toBeLessThan(0);
    expect(model.edges).toHaveLength(2);
    expect(isGraphInputsVirtualNode(virtual)).toBe(true);
  });

  it('does not render the virtual node when the definition declares no inputs', () => {
    const model = buildOrchestrationFlowModel(definition, run);
    expect(
      model.nodes.some((node) => node.id === GRAPH_INPUTS_VIRTUAL_NODE_ID),
    ).toBe(false);
    expect(model.nodes.map((node) => node.type)).toEqual([
      'orchestrationComponent',
      'orchestrationComponent',
      'orchestrationComponent',
    ]);
  });

  it('keeps virtual Graph Inputs out of the executable node count and layout save set', () => {
    const model = buildOrchestrationFlowModel(
      {
        ...definition,
        inputs: [
          {
            inputId: 'request',
            contract: {
              dataType: 'pudding.content',
              mediaTypes: ['text/plain'],
              cardinality: 'one',
              deliveries: ['inline'],
            },
            requiredAtActivation: true,
          },
        ],
      },
      run,
    );
    const layoutNodes = model.nodes.filter(
      (node) => !isGraphInputsVirtualNode(node),
    );
    expect(layoutNodes.map((node) => node.id)).toEqual([
      'research',
      'proposal',
      'review',
    ]);
    const model2 = buildOrchestrationFlowModel(definition, run);
    expect(model2.nodes).toHaveLength(3);
    expect(model.nodes).toHaveLength(4);
  });

  it('projects catalog ports and typed React Flow handles', () => {
    const catalog: OrchestrationCatalog = {
      schemaVersion: '1',
      triggers: [],
      components: [
        {
          contractHash: 'hash-1',
          descriptor: {
            componentType: 'pudding.agent.subagent',
            version: '1',
            displayName: 'Sub Agent',
            category: 'agent',
            nodeKind: 'subAgent',
            executorId: 'subagent',
            sideEffect: 'none',
            requiredCapabilities: [],
            inputPorts: [
              {
                portId: 'request',
                displayName: 'Request',
                required: true,
                contract: {
                  dataType: 'pudding.text',
                  mediaTypes: ['text/plain'],
                  cardinality: 'one',
                  deliveries: ['inline'],
                },
              },
            ],
            outputPorts: [
              {
                portId: 'result',
                displayName: 'Result',
                required: true,
                contract: {
                  dataType: 'pudding.text',
                  mediaTypes: ['text/plain'],
                  cardinality: 'one',
                  deliveries: ['inline'],
                },
              },
            ],
          },
        },
      ],
    };
    const dataDefinition = {
      ...definition,
      edges: [
        {
          ...definition.edges[0],
          bindings: [
            {
              sourcePortId: 'result',
              sourcePath: '$',
              targetPortId: 'request',
              aggregation: 'replace' as const,
            },
          ],
        },
      ],
    };
    const model = buildOrchestrationFlowModel(
      dataDefinition,
      run,
      undefined,
      catalog,
    );
    expect(model.nodes[0].type).toBe('orchestrationComponent');
    expect(model.nodes[0].data.inputPorts.map((port) => port.portId)).toEqual([
      'request',
    ]);
    expect(model.edges[0]).toEqual(
      expect.objectContaining({
        sourceHandle: 'data:out:result',
        targetHandle: 'data:in:request',
      }),
    );
  });
});
