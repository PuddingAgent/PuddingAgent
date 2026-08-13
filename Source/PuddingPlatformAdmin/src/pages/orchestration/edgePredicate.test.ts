import {
  appendEdgeBindingDraft,
  buildEdgeReachabilityDiagnostics,
  buildEdgeRoutingPreview,
  isRestrictedSourcePath,
  patchEdgeBindingDraft,
  patchEdgePredicateDraft,
  predicateFormToModel,
  predicateModelToForm,
  removeEdgeBindingDraft,
  removeEdgePredicateFromDraft,
  resolveEdgeSourceContract,
} from './edgeEditor';
import type {
  OrchestrationCatalog,
  OrchestrationEdgeDefinition,
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

const catalog: OrchestrationCatalog = {
  schemaVersion: 'pudding.agent-orchestration/v2',
  components: [
    component(
      'source',
      [],
      [
        {
          portId: 'text',
          displayName: 'Text',
          required: true,
          contract: contract('pudding.text'),
        },
      ],
    ),
    component(
      'target',
      [
        {
          portId: 'request',
          displayName: 'Request',
          required: true,
          contract: contract('pudding.text'),
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
    ),
  ],
  triggers: [],
};

const definition = (
  overrides: Partial<OrchestrationGraphDefinition> = {},
  edges: OrchestrationEdgeDefinition[] = [],
): OrchestrationGraphDefinition => ({
  schemaVersion: 'pudding.agent-orchestration/v2',
  graphId: 'graph-1',
  revisionId: 'graph-1/r001',
  revision: 1,
  workspaceId: 'default',
  rootSessionId: 'session-1',
  createdByAgentId: 'agent-1',
  objective: 'Edge Inspector helpers',
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
  edges,
  metadata: {},
  createdAtUtc: '2026-08-13T00:00:00Z',
  ...overrides,
});

const controlEdge = (
  overrides: Partial<OrchestrationEdgeDefinition> = {},
): OrchestrationEdgeDefinition => ({
  edgeId: 'edge-1',
  fromNodeId: 'source',
  toNodeId: 'target',
  kind: 'control',
  condition: 'onSuccess',
  bindings: [],
  ...overrides,
});

describe('S2-B5-3a edge predicate helpers', () => {
  it('accepts only the restricted JSONPath subset for sourcePath', () => {
    expect(isRestrictedSourcePath('$')).toBe(true);
    expect(isRestrictedSourcePath('$.response')).toBe(true);
    expect(isRestrictedSourcePath('$.items[0].title')).toBe(true);
    expect(isRestrictedSourcePath('$[2]')).toBe(true);
    expect(isRestrictedSourcePath('')).toBe(false);
    expect(isRestrictedSourcePath('response')).toBe(false);
    expect(isRestrictedSourcePath('$.items()')).toBe(false);
    expect(isRestrictedSourcePath('$..recursive')).toBe(false);
    expect(isRestrictedSourcePath('function(x){return x}')).toBe(false);
  });

  it('converts model to form and back without losing fields', () => {
    const model = {
      evaluatorId: 'pudding.schema.gate',
      version: '1',
      contractHash: 'sha256:abc',
      sourcePortId: 'result',
      sourcePath: '$.output',
      parameters: { threshold: 0.5, mode: 'strict' },
    };
    const form = predicateModelToForm(model);
    expect(form.evaluatorId).toBe('pudding.schema.gate');
    expect(form.parametersText).toContain('"threshold"');

    const roundTrip = predicateFormToModel(form);
    expect(roundTrip.issues).toEqual([]);
    expect(roundTrip.predicate).toEqual(model);
  });

  it('returns the default form for a missing predicate', () => {
    expect(predicateModelToForm(undefined)).toEqual({
      evaluatorId: '',
      version: '',
      contractHash: '',
      sourcePortId: '',
      sourcePath: '$',
      parametersText: '{}',
    });
  });

  it('flags empty and malformed fields with basic format issues', () => {
    const issues = predicateFormToModel({
      evaluatorId: '',
      version: '1 0',
      contractHash: '',
      sourcePortId: '',
      sourcePath: '$.runScript()',
      parametersText: '{bad json',
    }).issues.map((issue) => issue.code).sort();
    expect(issues).toEqual(
      [
        'predicate.evaluator_required',
        'predicate.parameters_invalid',
        'predicate.source_path_invalid',
        'predicate.source_port_required',
        'predicate.version_format',
      ].sort(),
    );
  });

  it('rejects non-object JSON parameters', () => {
    const arrayIssues = predicateFormToModel({
      evaluatorId: 'e',
      version: '1',
      contractHash: '',
      sourcePortId: 'p',
      sourcePath: '$',
      parametersText: '[1,2,3]',
    }).issues;
    expect(arrayIssues.map((issue) => issue.code)).toEqual([
      'predicate.parameters_invalid',
    ]);
  });

  it('creates a default predicate when patching an edge without one', () => {
    const base = definition({}, [controlEdge()]);
    const next = patchEdgePredicateDraft(base, 'edge-1', {
      evaluatorId: 'pudding.schema.gate',
    });
    expect(next.edges[0].predicate).toEqual({
      evaluatorId: 'pudding.schema.gate',
      version: '',
      sourcePortId: '',
      sourcePath: '$',
      parameters: {},
    });
  });

  it('merges a partial patch into an existing predicate and keeps other edges', () => {
    const base = definition(
      {},
      [
        controlEdge({
          predicate: {
            evaluatorId: 'pudding.schema.gate',
            version: '1',
            sourcePortId: 'result',
            sourcePath: '$',
            parameters: {},
          },
        }),
        controlEdge({ edgeId: 'edge-2', fromNodeId: 'target', toNodeId: 'source' }),
      ],
    );
    const next = patchEdgePredicateDraft(base, 'edge-1', {
      version: '2',
      parameters: { threshold: 3 },
    });
    expect(next.edges[0].predicate).toEqual({
      evaluatorId: 'pudding.schema.gate',
      version: '2',
      sourcePortId: 'result',
      sourcePath: '$',
      parameters: { threshold: 3 },
    });
    expect(next.edges[1].predicate).toBeUndefined();
    expect(base.edges[0].predicate?.version).toBe('1');
  });

  it('removes the predicate from only the targeted edge', () => {
    const base = definition(
      {},
      [
        controlEdge({
          predicate: {
            evaluatorId: 'pudding.schema.gate',
            version: '1',
            sourcePortId: 'result',
            sourcePath: '$',
            parameters: {},
          },
        }),
        controlEdge({
          edgeId: 'edge-2',
          fromNodeId: 'target',
          toNodeId: 'source',
          predicate: {
            evaluatorId: 'other.gate',
            version: '1',
            sourcePortId: 'out',
            sourcePath: '$',
            parameters: {},
          },
        }),
      ],
    );
    const next = removeEdgePredicateFromDraft(base, 'edge-1');
    expect(next.edges[0].predicate).toBeUndefined();
    expect(next.edges[1].predicate?.evaluatorId).toBe('other.gate');
  });
});

describe('S2-B5-3a data binding helpers', () => {
  const dataEdge = (): OrchestrationEdgeDefinition => ({
    edgeId: 'edge-data',
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
  });

  it('patches one binding immutably', () => {
    const base = definition({}, [dataEdge()]);
    const next = patchEdgeBindingDraft(base, 'edge-data', 0, {
      sourcePath: '$.text',
    });
    expect(next.edges[0].bindings[0].sourcePath).toBe('$.text');
    expect(base.edges[0].bindings[0].sourcePath).toBe('$');
  });

  it('appends and removes bindings through draft CRUD', () => {
    const base = definition({}, [dataEdge()]);
    const withExtra = appendEdgeBindingDraft(base, 'edge-data');
    expect(withExtra.edges[0].bindings).toHaveLength(2);
    expect(withExtra.edges[0].bindings[1]).toEqual({
      sourcePortId: '',
      sourcePath: '$',
      targetPortId: '',
      aggregation: 'replace',
    });
    const removed = removeEdgeBindingDraft(withExtra, 'edge-data', 1);
    expect(removed.edges[0].bindings).toHaveLength(1);
  });

  it('resolves source/target contracts from the catalog', () => {
    const resolved = resolveEdgeSourceContract(
      definition({}, [dataEdge()]),
      catalog,
      dataEdge(),
    );
    expect(resolved.source?.dataType).toBe('pudding.text');
    expect(resolved.target?.dataType).toBe('pudding.text');
    expect(resolved.sourcePortName).toBe('Text');
    expect(resolved.targetPortName).toBe('Request');
  });

  it('returns empty contracts when the catalog is missing', () => {
    expect(
      resolveEdgeSourceContract(definition({}, [dataEdge()]), undefined, dataEdge()),
    ).toEqual({});
  });
});

describe('S2-B5-3a control edge read-only previews', () => {
  it('builds a stable routing preview for condition and predicate', () => {
    const plain = buildEdgeRoutingPreview(controlEdge());
    expect(plain[0]).toMatch(/condition=onSuccess/);
    expect(plain.some((line) => line.includes('无谓词'))).toBe(true);

    const withPredicate = buildEdgeRoutingPreview(
      controlEdge({
        predicate: {
          evaluatorId: 'pudding.schema.gate',
          version: '1',
          sourcePortId: 'result',
          sourcePath: '$',
          parameters: {},
        },
      }),
    );
    expect(
      withPredicate.some((line) => line.includes('pudding.schema.gate@1')),
    ).toBe(true);
    expect(
      withPredicate.some((line) => line.includes('纯函数判定')),
    ).toBe(true);
  });

  it('reports reachability only when an endpoint is unreachable from roots', () => {
    expect(
      buildEdgeReachabilityDiagnostics(definition({}, [controlEdge()]), controlEdge()),
    ).toEqual([]);

    // orphan <-> source forms a 2-node cycle: no root reaches either node, so
    // the under-test edge orphan -> target reports both endpoints unreachable.
    const orphan = definition(
      {
        nodes: [
          ...definition().nodes,
          {
            nodeId: 'orphan',
            kind: 'tool',
            title: 'Orphan',
            objective: 'Orphan',
            component: { componentType: 'target', version: '1' },
            executor: { kind: 'tool', toolId: 'x' },
            expectedOutputContract: 'pudding.text',
            configuration: {},
            permissionMode: 'readOnly',
            failureBehavior: 'failRun',
            maxAttempts: 1,
            metadata: {},
          },
        ],
      },
      [
        controlEdge({ fromNodeId: 'orphan' }),
        {
          edgeId: 'cycle-1',
          fromNodeId: 'source',
          toNodeId: 'orphan',
          kind: 'control',
          condition: 'onSuccess',
          bindings: [],
        },
        {
          edgeId: 'cycle-2',
          fromNodeId: 'orphan',
          toNodeId: 'source',
          kind: 'control',
          condition: 'onSuccess',
          bindings: [],
        },
      ],
    );
    const issues = buildEdgeReachabilityDiagnostics(
      orphan,
      controlEdge({ fromNodeId: 'orphan' }),
    );
    expect(issues.map((issue) => issue.code)).toEqual([
      'edge.source_unreachable',
      'edge.target_unreachable',
    ]);
  });
});
