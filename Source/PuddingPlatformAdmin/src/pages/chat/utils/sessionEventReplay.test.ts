import { normalizeConversationEventType } from '@/services/platform/api';
import { SessionNotFoundError } from '../hooks/sessionRuntimeCleanup';
import {
  listSessionEventsPage,
  normalizeSessionEvent,
} from './sessionEventReplay';

jest.mock('@/services/platform/api', () => ({
  normalizeConversationEventType: jest.fn((type: string) => type),
}));

describe('sessionEventReplay', () => {
  beforeEach(() => {
    localStorage.clear();
    jest.clearAllMocks();
  });

  it('normalizes persisted wrappers and JSON payloads', () => {
    const event = normalizeSessionEvent({
      Type: 'event',
      EventType: 'delta',
      Sequence: '42',
      RecordedAtUtc: '2026-07-21T00:00:00Z',
      Payload: JSON.stringify({ delta: 'hello' }),
    });

    expect(normalizeConversationEventType).toHaveBeenCalledWith('delta');
    expect(event).toMatchObject({
      type: 'delta',
      sequenceNum: 42,
      recordedAt: '2026-07-21T00:00:00Z',
      delta: 'hello',
    });
  });

  it('returns null for values without an event type', () => {
    expect(normalizeSessionEvent(null)).toBeNull();
    expect(normalizeSessionEvent({ Payload: '{}' })).toBeNull();
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
