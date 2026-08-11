import { encodeRevisionPath, parseSseChunk } from './api';
import type { OrchestrationGraphDefinition } from './types';

describe('orchestration SSE client', () => {
  it('keeps revision slashes as route separators while escaping every segment', () => {
    expect(encodeRevisionPath('graph A/rev#2')).toBe('graph%20A/rev%232');
  });

  it('parses split canonical frames and ignores heartbeat comments', () => {
    const first = parseSseChunk(
      '',
      ': heartbeat\n\nid: 7\nevent: orchestration.node.',
    );
    expect(first.frames).toEqual([]);

    const second = parseSseChunk(
      first.remainder,
      'started\ndata: {"runId":"run-1","eventType":"orchestration.node.started","sequence":7}\n\n',
    );
    expect(second.remainder).toBe('');
    expect(second.frames).toEqual([
      expect.objectContaining({
        id: '7',
        event: 'orchestration.node.started',
        data: expect.objectContaining({ runId: 'run-1', sequence: 7 }),
      }),
    ]);
  });

  it('isolates malformed JSON without dropping the following event', () => {
    const parsed = parseSseChunk(
      '',
      'event: bad\ndata: {nope}\n\nid: 8\nevent: good\ndata: {"sequence":8}\n\n',
    );
    expect(parsed.frames).toHaveLength(1);
    expect(parsed.frames[0]).toEqual(
      expect.objectContaining({ id: '8', event: 'good' }),
    );
  });
});

describe('orchestration definition JSON round-trip (graph inputs mirror)', () => {
  const sample = {
    schemaVersion: 'pudding.agent-orchestration/v2',
    graphId: 'design-review',
    revisionId: 'design-review/r002',
    revision: 2,
    parentRevisionId: 'design-review/r001',
    workspaceId: 'default',
    rootSessionId: 'session:design-review',
    createdByAgentId: 'default.global_general-assistant',
    objective: '研究设计请求、独立复核并等待用户确认',
    requiresExplicitActivation: true,
    maxConcurrency: 2,
    inputs: [
      {
        inputId: 'request',
        contract: {
          dataType: 'pudding.any',
          mediaTypes: [],
          cardinality: 'one',
          deliveries: ['inline', 'artifact'],
        },
        defaultValue: {
          dataType: 'pudding.text',
          contentType: 'text/plain',
          inlineValue: 'default request',
          artifacts: [],
        },
        requiredAtActivation: true,
      },
    ],
    triggers: [
      {
        triggerId: 'manual',
        trigger: {
          triggerType: 'pudding.trigger.manual',
          version: '1',
          contractHash: 'sha256:server-frozen-trigger-hash',
        },
        enabled: true,
        configuration: {},
        inputBindings: [{ sourcePath: '$.request', targetInputId: 'request' }],
      },
    ],
    nodes: [
      {
        nodeId: 'research',
        kind: 'subAgent',
        title: '研究',
        objective: '收集事实、案例和约束',
        component: { componentType: 'pudding.agent.subagent', version: '1' },
        graphInputBindings: [{ inputId: 'request', targetPortId: 'request' }],
        expectedOutputContract: 'pudding.content',
        configuration: {},
        permissionMode: 'readOnly',
        failureBehavior: 'failRun',
        maxAttempts: 2,
        metadata: {},
      },
    ],
    edges: [],
    metadata: {},
    createdAtUtc: '2026-08-10T00:00:00Z',
  } satisfies OrchestrationGraphDefinition;

  it('round-trips a revision JSON with inputs, triggers and graphInputBindings without losing fields', () => {
    const roundTripped: OrchestrationGraphDefinition = JSON.parse(
      JSON.stringify(sample),
    );

    expect(roundTripped).toEqual(sample);
    expect(roundTripped.inputs).toHaveLength(1);
    expect(roundTripped.inputs?.[0]).toEqual(
      expect.objectContaining({
        inputId: 'request',
        contract: {
          dataType: 'pudding.any',
          mediaTypes: [],
          cardinality: 'one',
          deliveries: ['inline', 'artifact'],
        },
        defaultValue: expect.objectContaining({
          inlineValue: 'default request',
        }),
        requiredAtActivation: true,
      }),
    );
    expect(roundTripped.triggers?.[0]).toEqual(
      expect.objectContaining({
        triggerId: 'manual',
        trigger: expect.objectContaining({
          triggerType: 'pudding.trigger.manual',
          version: '1',
          contractHash: 'sha256:server-frozen-trigger-hash',
        }),
        enabled: true,
        inputBindings: [{ sourcePath: '$.request', targetInputId: 'request' }],
      }),
    );
    expect(roundTripped.nodes?.[0].graphInputBindings).toEqual([
      { inputId: 'request', targetPortId: 'request' },
    ]);
  });

  it('round-trips legacy revision JSON without the new optional graph input fields', () => {
    const legacy = {
      schemaVersion: 'pudding.agent-orchestration/v2',
      graphId: 'graph-1',
      revisionId: 'graph-1/r001',
      revision: 1,
      workspaceId: 'default',
      rootSessionId: 'session-1',
      createdByAgentId: 'agent-1',
      objective: 'Legacy graph without inputs',
      requiresExplicitActivation: true,
      maxConcurrency: 1,
      nodes: [
        {
          nodeId: 'start',
          kind: 'humanInput',
          title: 'Start',
          objective: 'Collect input.',
          component: {
            componentType: 'pudding.control.human-input',
            version: '1',
          },
          expectedOutputContract: 'pudding.content',
          configuration: {},
          permissionMode: 'readOnly',
          failureBehavior: 'awaitDecision',
          maxAttempts: 1,
          metadata: {},
        },
      ],
      edges: [],
      metadata: {},
      createdAtUtc: '2026-08-09T00:00:00Z',
    } satisfies OrchestrationGraphDefinition;

    const roundTripped: OrchestrationGraphDefinition = JSON.parse(
      JSON.stringify(legacy),
    );
    expect(roundTripped).toEqual(legacy);
    expect('inputs' in roundTripped).toBe(false);
    expect('triggers' in roundTripped).toBe(false);
    expect('graphInputBindings' in roundTripped.nodes[0]).toBe(false);
  });
});
