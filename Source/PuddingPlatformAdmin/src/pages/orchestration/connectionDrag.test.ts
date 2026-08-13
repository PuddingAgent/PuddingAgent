import {
  beginConnectionDrag,
  buildHandleClassName,
  endConnectionDrag,
  getConnectionDragState,
  ORCHESTRATION_HANDLE_COMPATIBLE_CLASS,
  ORCHESTRATION_HANDLE_INCOMPATIBLE_CLASS,
  subscribeConnectionDrag,
} from './connectionDrag';

describe('S2-B5-2 connection drag store', () => {
  afterEach(() => {
    endConnectionDrag();
  });

  it('starts idle and tracks begin/end transitions', () => {
    expect(getConnectionDragState()).toEqual({
      connecting: false,
      startNodeId: null,
      startHandleId: null,
      compatibility: null,
    });

    beginConnectionDrag('source', 'data:out:text', {
      'target::data:in:request': true,
    });
    expect(getConnectionDragState()).toEqual({
      connecting: true,
      startNodeId: 'source',
      startHandleId: 'data:out:text',
      compatibility: { 'target::data:in:request': true },
    });

    endConnectionDrag();
    expect(getConnectionDragState().connecting).toBe(false);
    expect(getConnectionDragState().compatibility).toBeNull();
  });

  it('notifies subscribers on begin and end', () => {
    const events: string[] = [];
    const unsubscribe = subscribeConnectionDrag(() => {
      events.push(getConnectionDragState().connecting ? 'begin' : 'end');
    });
    beginConnectionDrag('a', 'control:out', {});
    endConnectionDrag();
    unsubscribe();
    expect(events).toEqual(['begin', 'end']);
  });

  it('endConnectionDrag is idempotent and does not notify when already idle', () => {
    const events: string[] = [];
    const unsubscribe = subscribeConnectionDrag(() => {
      events.push('notify');
    });
    endConnectionDrag();
    endConnectionDrag();
    unsubscribe();
    expect(events).toEqual([]);
  });
});

describe('S2-B5-2 buildHandleClassName', () => {
  const idle = {
    connecting: false,
    startNodeId: null,
    startHandleId: null,
    compatibility: null,
  };
  const active = {
    connecting: true,
    startNodeId: 'source',
    startHandleId: 'data:out:text',
    compatibility: {
      'target::data:in:request': true,
      'target::data:in:image': false,
    },
  };

  it('returns undefined when idle', () => {
    expect(buildHandleClassName('target', 'data:in:request', idle)).toBe(
      undefined,
    );
  });

  it('maps compatible and incompatible handles to their classes', () => {
    expect(
      buildHandleClassName('target', 'data:in:request', active),
    ).toBe(ORCHESTRATION_HANDLE_COMPATIBLE_CLASS);
    expect(
      buildHandleClassName('target', 'data:in:image', active),
    ).toBe(ORCHESTRATION_HANDLE_INCOMPATIBLE_CLASS);
  });

  it('returns undefined for handles outside the compatibility map', () => {
    expect(
      buildHandleClassName('target', 'data:out:text', active),
    ).toBeUndefined();
  });
});
