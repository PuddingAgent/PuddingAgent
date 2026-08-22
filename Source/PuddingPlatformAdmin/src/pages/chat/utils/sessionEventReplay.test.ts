import { SessionNotFoundError } from '../hooks/sessionRuntimeCleanup';
import {
  getMaxSessionEventSequenceNum,
  listSessionEventsPage,
  normalizeSessionEvent,
} from './sessionEventReplay';

describe('sessionEventReplay', () => {
  beforeEach(() => {
    localStorage.clear();
    jest.clearAllMocks();
  });

  it('normalizes canonical envelopes with passthrough event names', () => {
    // TR-01/CU-02：canonical 事件名直通，无 legacy 映射；occurredAt 为时间锚点。
    const event = normalizeSessionEvent({
      type: 'message.content.appended',
      eventId: 'evt-1',
      sequence: '42',
      occurredAt: '2026-07-21T00:00:00Z',
      turnId: 'turn-1',
      messageId: 'message-1',
      payload: { delta: 'hello' },
    });

    expect(event).toMatchObject({
      type: 'message.content.appended',
      eventId: 'evt-1',
      sequenceNum: 42,
      recordedAt: '2026-07-21T00:00:00Z',
      turnId: 'turn-1',
      messageId: 'message-1',
      delta: 'hello',
    });
  });

  it('parses JSON string payloads from persisted envelopes', () => {
    const event = normalizeSessionEvent({
      type: 'tool.call.requested',
      sequence: 7,
      payload: JSON.stringify({ name: 'shell', toolCallId: 'call-1' }),
    });

    expect(event).toMatchObject({
      type: 'tool.call.requested',
      sequenceNum: 7,
      name: 'shell',
      toolCallId: 'call-1',
    });
  });

  it('returns null for values without an event type', () => {
    expect(normalizeSessionEvent(null)).toBeNull();
    expect(normalizeSessionEvent({ Payload: '{}' })).toBeNull();
  });

  it('advances a full replay page to its greatest durable sequence', () => {
    expect(
      getMaxSessionEventSequenceNum([
        { sequence: 90182 },
        { SequenceNum: '90231' },
        { payload: {} },
      ]),
    ).toBe(90231);
    expect(getMaxSessionEventSequenceNum([{ payload: {} }])).toBeNull();
  });

  it('maps replay 404 responses to SessionNotFoundError', async () => {
    globalThis.fetch = jest.fn().mockResolvedValue({
      ok: false,
      status: 404,
    });

    await expect(
      listSessionEventsPage('missing', 0, 100),
    ).rejects.toBeInstanceOf(SessionNotFoundError);
  });
});
