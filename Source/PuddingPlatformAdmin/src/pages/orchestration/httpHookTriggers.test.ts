import {
  addHttpHookTrigger,
  buildHttpHookEndpoint,
  listHttpHookTriggers,
  removeHttpHookTrigger,
  setHttpHookEnabled,
} from './httpHookTriggers';
import type {
  OrchestrationCatalog,
  OrchestrationGraphDefinition,
} from './types';

const definition = (): OrchestrationGraphDefinition => ({
  schemaVersion: 'pudding.agent-orchestration/v2',
  graphId: 'graph hook',
  revisionId: 'graph hook/r001',
  revision: 1,
  workspaceId: 'default',
  rootSessionId: 'session-1',
  createdByAgentId: 'admin',
  objective: 'Hook test',
  requiresExplicitActivation: true,
  maxConcurrency: 1,
  inputs: [
    {
      inputId: 'request',
      contract: {
        dataType: 'pudding.content',
        mediaTypes: ['application/json'],
        cardinality: 'one',
        deliveries: ['inline'],
      },
    },
  ],
  triggers: [],
  nodes: [],
  edges: [],
  metadata: {},
  createdAtUtc: '2026-08-11T00:00:00Z',
});

const catalog: OrchestrationCatalog = {
  schemaVersion: 'pudding.agent-orchestration/v2',
  components: [],
  triggers: [
    {
      descriptor: {
        triggerType: 'pudding.trigger.webhook',
        version: '1',
        displayName: 'Webhook',
        category: 'network',
        executorId: 'pudding.trigger.webhook/v1',
      },
      contractHash: 'sha256:webhook',
    },
  ],
};

describe('HTTP Hook trigger authoring', () => {
  it('freezes the catalog contract and maps payload to a Graph Input', () => {
    const result = addHttpHookTrigger(definition(), catalog, {
      triggerId: 'debug-hook',
      targetInputId: 'request',
      sourcePath: '$.message',
    });

    expect(result.error).toBeUndefined();
    expect(listHttpHookTriggers(result.definition)).toEqual([
      expect.objectContaining({
        triggerId: 'debug-hook',
        trigger: {
          triggerType: 'pudding.trigger.webhook',
          version: '1',
          contractHash: 'sha256:webhook',
        },
        inputBindings: [{ sourcePath: '$.message', targetInputId: 'request' }],
      }),
    ]);
  });

  it('rejects duplicate IDs and supports disable/remove', () => {
    const added = addHttpHookTrigger(definition(), catalog, {
      triggerId: 'debug-hook',
    }).definition;
    expect(
      addHttpHookTrigger(added, catalog, { triggerId: 'DEBUG-HOOK' }).error,
    ).toContain('已存在');
    expect(
      listHttpHookTriggers(setHttpHookEnabled(added, 'debug-hook', false))[0]
        .enabled,
    ).toBe(false);
    expect(
      listHttpHookTriggers(removeHttpHookTrigger(added, 'debug-hook')),
    ).toEqual([]);
  });

  it('builds an explicit immutable revision endpoint', () => {
    expect(
      buildHttpHookEndpoint('graph hook', 'graph hook/r001', 'debug-hook'),
    ).toBe(
      '/api/orchestrations/hooks/graph%20hook/debug-hook?revisionId=graph%20hook%2Fr001',
    );
  });
});
