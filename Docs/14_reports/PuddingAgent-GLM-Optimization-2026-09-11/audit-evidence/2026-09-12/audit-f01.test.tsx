import { act, renderHook } from '@testing-library/react';
import { useTurnSurfaceStore } from '../../Source/PuddingPlatformAdmin/src/pages/chat/hooks/useTurnSurfaceStore';
import { DetailHydrationScheduler } from '../../Source/PuddingPlatformAdmin/src/pages/chat/runtime/detailHydrationScheduler';
import { getAgentMessageProcessItems } from '../../Source/PuddingPlatformAdmin/src/pages/chat/client/agentChatApi';

jest.mock('../../Source/PuddingPlatformAdmin/src/pages/chat/client/agentChatApi', () => ({
  getAgentMessageProcessItems: jest.fn(() => new Promise(() => {})),
}));
const api = getAgentMessageProcessItems as jest.Mock;
const flush = async () => { for (let i = 0; i < 8; i++) await Promise.resolve(); };
const request = { workspaceId: 'w', agentId: 'a', conversationId: 's', messageId: 'm', detailsRevision: 1 };
const props = { workspaceId: 'w', agentId: 'a', conversationView: {
  workspaceId: 'w', ownerUserId: 'u', agentId: 'a', mainSessionId: 's', messages: [{
    messageId: 'm', turnId: 't', runId: 'r', role: 'agent', sourceId: 'a', sourceName: 'A',
    createdAt: '2026-09-11T00:00:00Z', content: 'answer', status: 'succeeded', processItems: [],
    processSummary: { hasDetails: true, totalItems: 1, thinkingRounds: 0, thinkingSteps: 0,
      toolCalls: 0, toolResults: 0, failedTools: 0, durationMs: 0 },
  }], activeRun: null, eventCursor: 1, updatedAt: '2026-09-11T00:00:00Z',
} } as any;

describe('Independent F01 acceptance probes', () => {
  beforeEach(() => api.mockClear());
  it.each([401, 404, 503])('retains failure budget through viewport exit/reentry, HTTP %s', async (status) => {
    const fetcher = jest.fn(async () => { throw { response: { status } }; });
    const scheduler = new DetailHydrationScheduler({ fetch: fetcher, maxAttempts: 1,
      onSuccess: jest.fn(), onFailure: jest.fn() });
    try {
      scheduler.sync([request]);
      await flush();
      expect(fetcher).toHaveBeenCalledTimes(1);
      scheduler.sync([]);
      scheduler.sync([request]);
      await flush();
      expect(fetcher).toHaveBeenCalledTimes(1);
    } finally { scheduler.dispose(); }
  });
  it('hydrates after React StrictMode effect cleanup/setup', async () => {
    const { result } = renderHook(() => useTurnSurfaceStore(props), {
      reactStrictMode: true,
    });
    act(() => result.current.registerVisibleTurn('t'));
    await flush();
    expect(api).toHaveBeenCalledTimes(1);
  });
  it('passes a cancellation signal from scheduler into API adapter', () => {
    const { result, unmount } = renderHook(() => useTurnSurfaceStore(props));
    act(() => result.current.registerVisibleTurn('t'));
    expect(api).toHaveBeenCalledTimes(1);
    const options = api.mock.calls[0][3];
    const signal = options instanceof AbortSignal ? options : options?.signal;
    unmount();
    expect(signal).toBeInstanceOf(AbortSignal);
    expect(signal.aborted).toBe(true);
  });
});
