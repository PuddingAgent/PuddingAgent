import { ExecutionFlowProjectionIndex } from './executionFlowProjectionIndex';
import type { ExecutionFlowEvent } from './executionFlowProjector';

const occurredAt = '2026-08-27T00:00:00.000Z';

const contentEvent = (
  turnId: string,
  sequence: number,
  delta = `delta-${sequence}`,
): ExecutionFlowEvent =>
  ({
    eventId: `${turnId}:${sequence}`,
    sequence,
    occurredAt,
    runId: `run:${turnId}`,
    turnId,
    type: 'message.content.appended',
    delta,
  }) as ExecutionFlowEvent;

const terminalEvent = (turnId: string, sequence: number): ExecutionFlowEvent =>
  ({
    eventId: `${turnId}:${sequence}`,
    sequence,
    occurredAt,
    runId: `run:${turnId}`,
    turnId,
    type: 'turn.completed',
    reply: 'done',
  }) as ExecutionFlowEvent;

describe('ExecutionFlowProjectionIndex', () => {
  it('reprojects only dirty turns and preserves all other projection identities', () => {
    const index = new ExecutionFlowProjectionIndex();
    index.enqueue(
      Array.from({ length: 100 }, (_, turn) =>
        contentEvent(`turn-${turn}`, turn + 1),
      ),
    );
    const firstFlush = index.flush();
    expect(firstFlush.changedTurnIds).toHaveLength(100);
    const before = index.getSnapshot();
    const unchangedBefore = before.get('turn-0');
    const changedBefore = before.get('turn-50');

    index.enqueue([contentEvent('turn-50', 1_001, 'updated')]);
    const secondFlush = index.flush();
    const after = index.getSnapshot();

    expect(secondFlush.changedTurnIds).toEqual(['turn-50']);
    expect(after).not.toBe(before);
    expect(after.get('turn-0')).toBe(unchangedBefore);
    expect(after.get('turn-50')).not.toBe(changedBefore);
    expect(after.get('turn-50')?.nodes[0]).toMatchObject({
      kind: 'message',
      text: 'delta-51updated',
    });
  });

  it('deduplicates pending events before projection', () => {
    const index = new ExecutionFlowProjectionIndex();
    const event = contentEvent('turn-1', 1);
    expect(index.enqueue([event, event])).toBe(1);
    expect(index.flush().changedTurnIds).toEqual(['turn-1']);
    expect(index.enqueue([event])).toBe(0);
    expect(index.flush().changed).toBe(false);
  });

  it('compacts terminal turns and rejects late progress', () => {
    const index = new ExecutionFlowProjectionIndex();
    index.enqueue([contentEvent('turn-1', 1), terminalEvent('turn-1', 2)]);
    index.flush();

    expect(index.getProjection('turn-1')?.terminal?.state).toBe('completed');
    expect(index.getStats()).toMatchObject({
      turns: 1,
      activeEvents: 0,
      terminalTurns: 1,
    });
    expect(index.enqueue([contentEvent('turn-1', 3, 'late')])).toBe(0);
    expect(index.flush().changed).toBe(false);
  });

  it('uses reset as a hard session boundary', () => {
    const index = new ExecutionFlowProjectionIndex();
    index.enqueue([contentEvent('turn-1', 1)]);
    index.flush();

    expect(index.reset()).toBe(true);
    expect(index.getSnapshot().size).toBe(0);
    expect(index.getStats()).toMatchObject({
      turns: 0,
      activeEvents: 0,
      pendingEvents: 0,
    });
  });
});
