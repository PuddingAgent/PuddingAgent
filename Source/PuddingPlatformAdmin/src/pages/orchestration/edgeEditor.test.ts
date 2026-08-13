import {
  areDataContractsCompatible,
  buildEdgeFromConnection,
  parseOrchestrationHandle,
  patchEdgeDraft,
  removeEdgeFromDraft,
} from './edgeEditor';
import type {
  OrchestrationCatalog,
  OrchestrationGraphDefinition,
  OrchestrationRegisteredComponent,
} from './types';

const contract = (
  dataType: string,
  cardinality: 'one' | 'many' = 'one',
  mediaTypes: string[] = [],
  deliveries: Array<'inline' | 'artifact' | 'stream' | 'event'> = ['inline'],
) => ({ dataType, cardinality, mediaTypes, deliveries });

const component = (
  componentType: string,
  inputPorts: OrchestrationRegisteredComponent['descriptor']['inputPorts'],
  outputPorts: OrchestrationRegisteredComponent['descriptor']['outputPorts'],
): OrchestrationRegisteredComponent => ({
  contractHash: `sha256:${componentType}`,
  descriptor: {
    componentType,
    version: '1',
    displayName: componentType,
    category: 'test',
    nodeKind: 'tool',
    executorId: `${componentType}/v1`,
    sideEffect: 'none',
    inputPorts,
    outputPorts,
    requiredCapabilities: [],
  },
});

const sourceComponent = component(
  'source',
  [],
  [
    {
      portId: 'text',
      displayName: 'Text',
      required: true,
      contract: contract('pudding.text'),
    },
    {
      portId: 'images',
      displayName: 'Images',
      required: true,
      contract: contract('pudding.artifact', 'many', ['image/*'], ['artifact']),
    },
  ],
);
const targetComponent = component(
  'target',
  [
    {
      portId: 'request',
      displayName: 'Request',
      required: true,
      contract: contract('pudding.text'),
    },
    {
      portId: 'image',
      displayName: 'Image',
      required: false,
      contract: contract(
        'pudding.artifact',
        'one',
        ['image/png'],
        ['artifact'],
      ),
    },
    {
      portId: 'contexts',
      displayName: 'Contexts',
      required: false,
      contract: contract('pudding.text', 'many'),
    },
  ],
  [
    {
      portId: 'result',
      displayName: 'Result',
      required: true,
      contract: contract('pudding.text'),
    },
  ],
);

const catalog: OrchestrationCatalog = {
  schemaVersion: 'pudding.agent-orchestration/v2',
  components: [sourceComponent, targetComponent],
  triggers: [],
};

const definition = (
  overrides: Partial<OrchestrationGraphDefinition> = {},
): OrchestrationGraphDefinition => ({
  schemaVersion: 'pudding.agent-orchestration/v2',
  graphId: 'graph-1',
  revisionId: 'graph-1/r001',
  revision: 1,
  workspaceId: 'default',
  rootSessionId: 'session-1',
  createdByAgentId: 'agent-1',
  objective: 'Connect typed ports',
  requiresExplicitActivation: true,
  maxConcurrency: 2,
  nodes: [
    {
      nodeId: 'source',
      kind: 'tool',
      title: 'Source',
      objective: 'Source',
      component: { componentType: 'source', version: '1' },
      executor: { kind: 'tool', toolId: 'source' },
      expectedOutputContract: 'pudding.text',
      configuration: {},
      permissionMode: 'readOnly',
      failureBehavior: 'failRun',
      maxAttempts: 1,
      metadata: {},
    },
    {
      nodeId: 'target',
      kind: 'tool',
      title: 'Target',
      objective: 'Target',
      component: { componentType: 'target', version: '1' },
      executor: { kind: 'tool', toolId: 'target' },
      expectedOutputContract: 'pudding.text',
      configuration: {},
      permissionMode: 'readOnly',
      failureBehavior: 'failRun',
      maxAttempts: 1,
      metadata: {},
    },
  ],
  edges: [],
  metadata: {},
  createdAtUtc: '2026-08-11T00:00:00Z',
  ...overrides,
});

describe('orchestration edge editor', () => {
  it('parses control and typed data handles', () => {
    expect(parseOrchestrationHandle('control:out')).toEqual({
      kind: 'control',
      direction: 'out',
    });
    expect(parseOrchestrationHandle('data:in:request')).toEqual({
      kind: 'data',
      direction: 'in',
      portId: 'request',
    });
    expect(parseOrchestrationHandle('invalid')).toBeUndefined();
  });

  it('builds a control edge from control handles', () => {
    const result = buildEdgeFromConnection(
      definition(),
      catalog,
      {
        source: 'source',
        sourceHandle: 'control:out',
        target: 'target',
        targetHandle: 'control:in',
      },
      'edge-control',
    );
    expect(result.error).toBeUndefined();
    expect(result.edge).toEqual(
      expect.objectContaining({
        edgeId: 'edge-control',
        kind: 'control',
        condition: 'onSuccess',
        bindings: [],
      }),
    );
  });

  it('builds data edges with replace for one and append for many', () => {
    const one = buildEdgeFromConnection(
      definition(),
      catalog,
      {
        source: 'source',
        sourceHandle: 'data:out:text',
        target: 'target',
        targetHandle: 'data:in:request',
      },
      'edge-one',
    );
    expect(one.error).toBeUndefined();
    expect(one.edge?.bindings).toEqual([
      {
        sourcePortId: 'text',
        sourcePath: '$',
        targetPortId: 'request',
        aggregation: 'replace',
      },
    ]);

    const many = buildEdgeFromConnection(
      definition(),
      catalog,
      {
        source: 'source',
        sourceHandle: 'data:out:text',
        target: 'target',
        targetHandle: 'data:in:contexts',
      },
      'edge-many',
    );
    expect(many.edge?.bindings[0].aggregation).toBe('append');
  });

  it('rejects data type and many-to-one incompatibility', () => {
    const typeMismatch = buildEdgeFromConnection(
      definition(),
      catalog,
      {
        source: 'source',
        sourceHandle: 'data:out:text',
        target: 'target',
        targetHandle: 'data:in:image',
      },
      'edge-bad-type',
    );
    expect(typeMismatch.error?.code).toBe('edge.ports_incompatible');

    const cardinalityMismatch = buildEdgeFromConnection(
      definition(),
      catalog,
      {
        source: 'source',
        sourceHandle: 'data:out:images',
        target: 'target',
        targetHandle: 'data:in:image',
      },
      'edge-bad-cardinality',
    );
    expect(cardinalityMismatch.error?.code).toBe('edge.ports_incompatible');
  });

  it('rejects a second provider for a single target port, including graph input bindings', () => {
    const occupied = definition({
      nodes: definition().nodes.map((node) =>
        node.nodeId === 'target'
          ? {
              ...node,
              graphInputBindings: [
                { inputId: 'request', targetPortId: 'request' },
              ],
            }
          : node,
      ),
    });
    const result = buildEdgeFromConnection(
      occupied,
      catalog,
      {
        source: 'source',
        sourceHandle: 'data:out:text',
        target: 'target',
        targetHandle: 'data:in:request',
      },
      'edge-occupied',
    );
    expect(result.error?.code).toBe('edge.target_port_occupied');
  });

  it('rejects self loops and cycles across control/data edges', () => {
    const self = buildEdgeFromConnection(
      definition(),
      catalog,
      {
        source: 'source',
        sourceHandle: 'control:out',
        target: 'source',
        targetHandle: 'control:in',
      },
      'edge-self',
    );
    expect(self.error?.code).toBe('edge.self_loop');

    const base = definition({
      edges: [
        {
          edgeId: 'target-source',
          fromNodeId: 'target',
          toNodeId: 'source',
          kind: 'control',
          condition: 'onSuccess',
          bindings: [],
        },
      ],
    });
    const cycle = buildEdgeFromConnection(
      base,
      catalog,
      {
        source: 'source',
        sourceHandle: 'control:out',
        target: 'target',
        targetHandle: 'control:in',
      },
      'edge-cycle',
    );
    expect(cycle.error?.code).toBe('edge.cycle');
  });

  it('patches and removes an edge immutably', () => {
    const base = definition({
      edges: [
        {
          edgeId: 'edge-1',
          fromNodeId: 'source',
          toNodeId: 'target',
          kind: 'control',
          condition: 'onSuccess',
          bindings: [],
        },
      ],
    });
    const patched = patchEdgeDraft(base, 'edge-1', {
      condition: 'onCompletion',
    });
    expect(patched.edges[0].condition).toBe('onCompletion');
    expect(base.edges[0].condition).toBe('onSuccess');
    expect(removeEdgeFromDraft(patched, 'edge-1').edges).toEqual([]);
  });

  // S2-B6' B5-1 gap closure: direct branch tests for MIME wildcard,
  // delivery-mode rejection and duplicate connection rejection.
  it('accepts MIME wildcard media types and rejects disjoint ones', () => {
    // application/* accepts application/json (wildcard suffix match).
    expect(
      areDataContractsCompatible(
        contract('pudding.artifact', 'one', ['application/json'], ['artifact']),
        contract('pudding.artifact', 'one', ['application/*'], ['artifact']),
      ),
    ).toBe(true);
    // */* accepts any media type.
    expect(
      areDataContractsCompatible(
        contract('pudding.artifact', 'one', ['image/png'], ['artifact']),
        contract('pudding.artifact', 'one', ['*/*'], ['artifact']),
      ),
    ).toBe(true);
    // Exact match still holds.
    expect(
      areDataContractsCompatible(
        contract('pudding.artifact', 'one', ['image/png'], ['artifact']),
        contract('pudding.artifact', 'one', ['image/png'], ['artifact']),
      ),
    ).toBe(true);
    // 85 §6.1 MIME reject row: audio/mpeg -> image/* must be rejected.
    expect(
      areDataContractsCompatible(
        contract('pudding.artifact', 'one', ['audio/mpeg'], ['artifact']),
        contract('pudding.artifact', 'one', ['image/*'], ['artifact']),
      ),
    ).toBe(false);
    // Wildcard must not bridge unrelated families: image/* vs audio/*.
    expect(
      areDataContractsCompatible(
        contract('pudding.artifact', 'one', ['image/png'], ['artifact']),
        contract('pudding.artifact', 'one', ['audio/*'], ['artifact']),
      ),
    ).toBe(false);
  });

  it('rejects delivery mode mismatch with no shared delivery', () => {
    // 85 §6.1 delivery reject row: source artifact / target inline only.
    expect(
      areDataContractsCompatible(
        contract('pudding.artifact', 'one', ['image/png'], ['artifact']),
        contract('pudding.artifact', 'one', ['image/png'], ['inline']),
      ),
    ).toBe(false);
    // Shared artifact delivery (even with extra options) accepts.
    expect(
      areDataContractsCompatible(
        contract('pudding.artifact', 'one', ['image/png'], ['artifact']),
        contract('pudding.artifact', 'one', ['image/png'], ['artifact', 'stream']),
      ),
    ).toBe(true);
  });

  it('rejects duplicate control and data edges between the same ports', () => {
    const controlBase = definition({
      edges: [
        {
          edgeId: 'edge-control-1',
          fromNodeId: 'source',
          toNodeId: 'target',
          kind: 'control',
          condition: 'onSuccess',
          bindings: [],
        },
      ],
    });
    const dupControl = buildEdgeFromConnection(
      controlBase,
      catalog,
      {
        source: 'source',
        sourceHandle: 'control:out',
        target: 'target',
        targetHandle: 'control:in',
      },
      'edge-control-2',
    );
    expect(dupControl.error?.code).toBe('edge.duplicate');

    const dataBase = definition({
      edges: [
        {
          edgeId: 'edge-data-1',
          fromNodeId: 'source',
          toNodeId: 'target',
          kind: 'data',
          condition: 'onSuccess',
          bindings: [
            {
              sourcePortId: 'text',
              sourcePath: '$',
              targetPortId: 'request',
              aggregation: 'replace',
            },
          ],
        },
      ],
    });
    const dupData = buildEdgeFromConnection(
      dataBase,
      catalog,
      {
        source: 'source',
        sourceHandle: 'data:out:text',
        target: 'target',
        targetHandle: 'data:in:request',
      },
      'edge-data-2',
    );
    expect(dupData.error?.code).toBe('edge.duplicate');
    expect(dupData.edge).toBeUndefined();
  });
});
