import {
  buildFailedEdgeClass,
  buildFailedEdgeStroke,
  buildHandleCompatibilityMap,
  collectEdgeValidationFailures,
  isConnectionValid,
  ORCHESTRATION_EDGE_FAILED_CLASS,
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

describe('S2-B5-2 connection validation wrappers', () => {
  it('isConnectionValid accepts compatible control and data pairs', () => {
    expect(
      isConnectionValid(definition(), catalog, {
        source: 'source',
        sourceHandle: 'control:out',
        target: 'target',
        targetHandle: 'control:in',
      }),
    ).toBe(true);
    expect(
      isConnectionValid(definition(), catalog, {
        source: 'source',
        sourceHandle: 'data:out:text',
        target: 'target',
        targetHandle: 'data:in:request',
      }),
    ).toBe(true);
  });

  it('isConnectionValid rejects incompatible port pairs and self loops', () => {
    expect(
      isConnectionValid(definition(), catalog, {
        source: 'source',
        sourceHandle: 'data:out:text',
        target: 'target',
        targetHandle: 'data:in:image',
      }),
    ).toBe(false);
    expect(
      isConnectionValid(definition(), catalog, {
        source: 'source',
        sourceHandle: 'control:out',
        target: 'source',
        targetHandle: 'control:in',
      }),
    ).toBe(false);
    expect(
      isConnectionValid(definition(), catalog, {
        source: 'source',
        sourceHandle: 'control:out',
        target: 'target',
        targetHandle: 'data:in:request',
      }),
    ).toBe(false);
  });

  it('isConnectionValid rejects occupied single-value target ports', () => {
    const occupied = definition({
      edges: [
        {
          edgeId: 'edge-a',
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
    expect(
      isConnectionValid(occupied, catalog, {
        source: 'source',
        sourceHandle: 'data:out:text',
        target: 'target',
        targetHandle: 'data:in:request',
      }),
    ).toBe(false);
  });

  it('buildHandleCompatibilityMap keys every opposite-direction handle with validity', () => {
    const map = buildHandleCompatibilityMap(
      definition(),
      catalog,
      'source',
      'data:out:text',
    );
    expect(map['target::data:in:request']).toBe(true);
    expect(map['target::data:in:image']).toBe(false);
    expect(map['target::data:in:contexts']).toBe(true);
    // Control handle exists but is a kind mismatch against a data source.
    expect(map['target::control:in']).toBe(false);
    // Same-direction handles are not candidates.
    expect(map['source::control:out']).toBeUndefined();
    expect(map['source::data:out:text']).toBeUndefined();
    // The source node's own input handles are candidates but self loops fail.
    expect(map['source::control:in']).toBe(false);
    expect(map['source::data:in:request']).toBeUndefined(); // unknown port for source component
  });

  it('buildHandleCompatibilityMap supports reverse drags starting from an input handle', () => {
    const map = buildHandleCompatibilityMap(
      definition(),
      catalog,
      'target',
      'data:in:request',
    );
    expect(map['source::data:out:text']).toBe(true);
    // Control handle exists but is a kind mismatch against a data source.
    expect(map['source::control:out']).toBe(false);
    expect(map['source::data:out:images']).toBe(false);
  });

  it('buildHandleCompatibilityMap returns an empty map for invalid start handles', () => {
    expect(
      buildHandleCompatibilityMap(definition(), catalog, null, 'control:out'),
    ).toEqual({});
    expect(
      buildHandleCompatibilityMap(definition(), catalog, 'source', null),
    ).toEqual({});
    expect(
      buildHandleCompatibilityMap(definition(), catalog, 'source', 'bogus'),
    ).toEqual({});
  });
});

describe('S2-B5-2 failed-edge styling wrappers', () => {
  it('collectEdgeValidationFailures keeps error-severity edge issues by elementId', () => {
    const issues = [
      {
        code: 'graph.edge_binding_source_port_incompatible',
        message: 'boom',
        severity: 'error' as const,
        elementType: 'edge',
        elementId: 'edge-1',
        portId: 'text',
      },
      {
        code: 'graph.edge_warning',
        message: 'warn',
        severity: 'warning' as const,
        elementType: 'edge',
        elementId: 'edge-2',
      },
      {
        code: 'graph.node_input_port_incompatible',
        message: 'node-level',
        severity: 'error' as const,
        elementType: 'node',
        elementId: 'node-1',
      },
      {
        code: 'graph.edge_duplicate',
        message: 'no element projection',
        severity: 'error' as const,
      },
    ];
    const failed = collectEdgeValidationFailures(issues);
    expect([...failed]).toEqual(['edge-1']);
  });

  it('collectEdgeValidationFailures tolerates undefined issues', () => {
    expect([...collectEdgeValidationFailures(undefined)]).toEqual([]);
  });

  it('buildFailedEdgeClass and buildFailedEdgeStroke mark exactly the failed edges', () => {
    const failed = collectEdgeValidationFailures([
      {
        code: 'graph.edge_cycle',
        message: 'cycle',
        severity: 'error',
        elementType: 'edge',
        elementId: 'edge-bad',
      },
    ]);
    expect(buildFailedEdgeClass('edge-bad', failed)).toBe(
      ORCHESTRATION_EDGE_FAILED_CLASS,
    );
    expect(buildFailedEdgeStroke('edge-bad', failed)).toEqual({
      stroke: '#ff4d4f',
    });
    expect(buildFailedEdgeClass('edge-ok', failed)).toBeUndefined();
    expect(buildFailedEdgeStroke('edge-ok', failed)).toBeUndefined();
  });
});
