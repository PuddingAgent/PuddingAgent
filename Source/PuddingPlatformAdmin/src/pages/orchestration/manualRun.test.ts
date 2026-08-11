import { buildManualRunInputs } from './manualRun';
import type { OrchestrationGraphDefinition } from './types';

const definition: OrchestrationGraphDefinition = {
  schemaVersion: 'pudding.agent-orchestration/v2',
  graphId: 'image-graph',
  revisionId: 'image-graph/r001',
  revision: 1,
  workspaceId: 'default',
  rootSessionId: 'root',
  createdByAgentId: 'admin',
  objective: 'Generate an image',
  requiresExplicitActivation: true,
  maxConcurrency: 1,
  inputs: [
    {
      inputId: 'prompt',
      contract: {
        dataType: 'pudding.content',
        mediaTypes: ['text/plain'],
        cardinality: 'one',
        deliveries: ['inline'],
      },
    },
    {
      inputId: 'settings',
      contract: {
        dataType: 'pudding.json',
        mediaTypes: ['application/json'],
        cardinality: 'one',
        deliveries: ['inline'],
      },
      requiredAtActivation: false,
    },
  ],
  nodes: [],
  edges: [],
  metadata: {},
  createdAtUtc: '2026-08-11T00:00:00Z',
};

describe('manual orchestration run inputs', () => {
  it('creates typed value envelopes and omits empty optional values', () => {
    expect(
      buildManualRunInputs(definition, {
        prompt: 'a cinematic lighthouse',
        settings: '',
      }),
    ).toEqual({
      prompt: {
        dataType: 'pudding.content',
        contentType: 'text/plain',
        inlineValue: 'a cinematic lighthouse',
      },
    });
  });

  it('parses json graph inputs before submission', () => {
    expect(
      buildManualRunInputs(definition, {
        prompt: 'test',
        settings: '{"seed":42}',
      }).settings.inlineValue,
    ).toEqual({ seed: 42 });
  });

  it('rejects malformed json before starting a run', () => {
    expect(() =>
      buildManualRunInputs(definition, {
        prompt: 'test',
        settings: '{bad}',
      }),
    ).toThrow('JSON 输入格式不正确');
  });
});
