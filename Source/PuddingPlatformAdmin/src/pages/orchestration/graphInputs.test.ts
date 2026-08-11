import {
  addGraphInput,
  listInputReferences,
  removeGraphInput,
  updateGraphInput,
} from './graphInputs';
import type {
  OrchestrationGraphDefinition,
  OrchestrationGraphInput,
  OrchestrationDataContract,
  OrchestrationNodeDefinition,
} from './types';

function definition(overrides: Partial<OrchestrationGraphDefinition> = {}): OrchestrationGraphDefinition {
  return {
    schemaVersion: 'pudding.agent-orchestration/v2',
    graphId: 'graph-1',
    revisionId: 'graph-1/r001',
    revision: 1,
    workspaceId: 'default',
    rootSessionId: 'session-1',
    createdByAgentId: 'agent-1',
    objective: 'Initial objective',
    requiresExplicitActivation: true,
    maxConcurrency: 1,
    nodes: [],
    edges: [],
    metadata: {},
    createdAtUtc: '2026-08-10T00:00:00Z',
    ...overrides,
  };
}

function node(nodeId: string, graphInputBindings: OrchestrationNodeDefinition['graphInputBindings'] = []): OrchestrationNodeDefinition {
  return {
    nodeId,
    kind: 'subAgent',
    title: nodeId,
    objective: `${nodeId} objective`,
    component: { componentType: 'pudding.agent.subagent', version: '1' },
    expectedOutputContract: 'pudding.content',
    configuration: {},
    permissionMode: 'readOnly',
    failureBehavior: 'failRun',
    maxAttempts: 1,
    metadata: {},
    graphInputBindings,
  };
}

const anyTextContract: OrchestrationDataContract = {
  dataType: 'pudding.content',
  mediaTypes: ['text/plain'],
  cardinality: 'one',
  deliveries: ['inline', 'artifact'],
};

function input(inputId: string, overrides: Partial<OrchestrationGraphInput> = {}): OrchestrationGraphInput {
  return {
    inputId,
    contract: anyTextContract,
    requiredAtActivation: true,
    ...overrides,
  };
}

describe('orchestration graph inputs (pure layer)', () => {
  it('adds a graph input to a definition without inputs', () => {
    const next = addGraphInput(definition(), input('request'));
    expect(next.inputs).toEqual([input('request')]);
  });

  it('appends to existing inputs and leaves other sections untouched', () => {
    const base = definition({
      inputs: [input('context')],
      nodes: [node('research', [{ inputId: 'context', targetPortId: 'context' }])],
    });
    const next = addGraphInput(base, input('request'));
    expect(next.inputs?.map((item) => item.inputId)).toEqual(['context', 'request']);
    expect(next.nodes).toBe(base.nodes);
    expect(next.edges).toBe(base.edges);
  });

  it('deduplicates by inputId ignoring case and whitespace, returning the same definition', () => {
    const base = definition({ inputs: [input('request')] });
    expect(addGraphInput(base, input('Request'))).toBe(base);
    expect(addGraphInput(base, input('  request  '))).toBe(base);
    expect(base.inputs).toHaveLength(1);
  });

  it('keeps the source definition immutable when adding', () => {
    const base = definition();
    addGraphInput(base, input('request'));
    expect(base.inputs).toBeUndefined();
  });

  it('updates an input by id (case-insensitive) while preserving inputId', () => {
    const base = definition({ inputs: [input('request')] });
    const next = updateGraphInput(base, 'REQUEST', {
      contract: { ...anyTextContract, cardinality: 'many' },
      requiredAtActivation: false,
    });
    expect(next.inputs).toHaveLength(1);
    expect(next.inputs?.[0]).toEqual(
      expect.objectContaining({
        inputId: 'request',
        contract: expect.objectContaining({ cardinality: 'many' }),
        requiredAtActivation: false,
      }),
    );
  });

  it('does not mutate the original input object when updating', () => {
    const base = definition({ inputs: [input('request')] });
    updateGraphInput(base, 'request', { requiredAtActivation: false });
    expect(base.inputs?.[0].requiredAtActivation).toBe(true);
  });

  it('no-ops when updating an unknown input id', () => {
    const base = definition({ inputs: [input('request')] });
    expect(updateGraphInput(base, 'missing', { requiredAtActivation: false })).toBe(base);
  });

  it('removes the input, cleans node bindings, and reports affected nodes and bindings', () => {
    const base = definition({
      inputs: [input('request'), input('context')],
      nodes: [
        node('research', [
          { inputId: 'request', targetPortId: 'request' },
          { inputId: 'context', targetPortId: 'context' },
        ]),
        node('review', [{ inputId: 'request', targetPortId: 'request', targetKey: 'nested' }]),
        node('confirm', []),
      ],
    });
    const result = removeGraphInput(base, 'request');
    expect(result.definition.inputs?.map((item) => item.inputId)).toEqual(['context']);
    expect(result.definition.nodes[0].graphInputBindings).toEqual([{ inputId: 'context', targetPortId: 'context' }]);
    expect(result.definition.nodes[1].graphInputBindings).toEqual([]);
    expect(result.definition.nodes[2].graphInputBindings).toEqual([]);
    expect(result.affectedNodeIds).toEqual(['research', 'review']);
    expect(result.affectedBindings).toEqual([
      { nodeId: 'research', binding: { inputId: 'request', targetPortId: 'request' } },
      { nodeId: 'review', binding: { inputId: 'request', targetPortId: 'request', targetKey: 'nested' } },
    ]);
  });

  it('no-ops when removing an unknown input id', () => {
    const base = definition({
      inputs: [input('request')],
      nodes: [node('research', [{ inputId: 'request', targetPortId: 'request' }])],
    });
    const result = removeGraphInput(base, 'missing');
    expect(result.definition).toBe(base);
    expect(result.affectedNodeIds).toEqual([]);
    expect(result.affectedBindings).toEqual([]);
  });

  it('keeps the source definition immutable when removing', () => {
    const base = definition({
      inputs: [input('request')],
      nodes: [node('research', [{ inputId: 'request', targetPortId: 'request' }])],
    });
    const originalNodes = base.nodes;
    removeGraphInput(base, 'request');
    expect(base.inputs?.map((item) => item.inputId)).toEqual(['request']);
    expect(base.nodes).toBe(originalNodes);
    expect(base.nodes[0].graphInputBindings).toHaveLength(1);
  });

  it('lists node references for an input id with targetKey', () => {
    const base = definition({
      nodes: [
        node('research', [{ inputId: 'request', targetPortId: 'request' }]),
        node('review', [{ inputId: 'request', targetPortId: 'context', targetKey: 'deep' }]),
        node('other', [{ inputId: 'context', targetPortId: 'context' }]),
      ],
    });
    expect(listInputReferences(base, 'request')).toEqual([
      { nodeId: 'research', targetPortId: 'request', targetKey: undefined },
      { nodeId: 'review', targetPortId: 'context', targetKey: 'deep' },
    ]);
  });

  it('lists references case-insensitively and returns [] for unknown ids', () => {
    const base = definition({
      nodes: [node('research', [{ inputId: 'REQUEST', targetPortId: 'request' }])],
    });
    expect(listInputReferences(base, 'request')).toEqual([
      { nodeId: 'research', targetPortId: 'request', targetKey: undefined },
    ]);
    expect(listInputReferences(base, 'missing')).toEqual([]);
  });
});
