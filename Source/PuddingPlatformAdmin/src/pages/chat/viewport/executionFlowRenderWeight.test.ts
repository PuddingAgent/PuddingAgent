import type { ExecutionFlowProjection } from '../projections/executionFlowProjector';
import { getExecutionFlowRenderWeight } from './executionFlowRenderWeight';

const emptyStats = {
  totalEvents: 0,
  projectedEvents: 0,
  duplicateEvents: 0,
  ignoredAfterTerminal: 0,
  protocolErrors: 0,
};

describe('getExecutionFlowRenderWeight', () => {
  it('charges both projected text and structural DOM cost', () => {
    const projection: ExecutionFlowProjection = {
      nodes: [
        {
          kind: 'message',
          key: 'message-1',
          sequence: 1,
          sourceEventIds: ['event-1'],
          text: 'x'.repeat(100),
          terminal: 'completed',
        },
        {
          kind: 'tool',
          key: 'tool-1',
          sequence: 2,
          sourceEventIds: ['event-2'],
          toolCallId: 'call-1',
          state: 'completed',
          placeholder: false,
          name: 'read_file',
          output: 'done',
          children: [],
        },
      ],
      stats: emptyStats,
      protocolErrors: [],
    };

    expect(getExecutionFlowRenderWeight(projection)).toBeGreaterThan(1_100);
    expect(getExecutionFlowRenderWeight(undefined)).toBe(0);
  });
});
