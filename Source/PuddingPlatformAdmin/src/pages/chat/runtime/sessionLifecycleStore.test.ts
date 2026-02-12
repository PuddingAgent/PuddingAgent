// ── Session Lifecycle 单测 ─────────────────────────────────────
// ADR-054 Step 2: 验证 reduceSessionLifecycle 状态机所有转换路径

import {
  canLoadAgentProjection,
  canStartLegacySse,
  createSessionTerminalEvent,
  mapReasonToTerminalReason,
  reduceSessionLifecycle,
} from './sessionLifecycleStore';
import type { SessionLifecycleState } from './types';
import { INITIAL_SESSION_LIFECYCLE } from './types';

describe('reduceSessionLifecycle', () => {
  describe('WORKSPACE_SELECTED', () => {
    it('enters idle phase with workspaceId', () => {
      const state = reduceSessionLifecycle(INITIAL_SESSION_LIFECYCLE, {
        type: 'WORKSPACE_SELECTED',
        workspaceId: 'ws-1',
      });
      expect(state.phase).toBe('idle');
      expect(state.workspaceId).toBe('ws-1');
      expect(state.sessionId).toBeUndefined();
    });
  });

  describe('AGENT_SELECTED', () => {
    it('sets agentId while in idle phase', () => {
      const state = reduceSessionLifecycle(
        { phase: 'idle', workspaceId: 'ws-1' },
        { type: 'AGENT_SELECTED', agentId: 'agent-a' },
      );
      expect(state.phase).toBe('idle');
      expect(state.agentId).toBe('agent-a');
    });
  });

  describe('MAIN_SESSION_RESOLVING', () => {
    it('transitions from idle to resolving', () => {
      const state = reduceSessionLifecycle(
        { phase: 'idle', workspaceId: 'ws-1' },
        {
          type: 'MAIN_SESSION_RESOLVING',
          workspaceId: 'ws-1',
          agentId: 'agent-a',
        },
      );
      expect(state.phase).toBe('resolving');
      expect(state.workspaceId).toBe('ws-1');
      expect(state.agentId).toBe('agent-a');
    });
  });

  describe('SESSION_ACTIVE', () => {
    it('transitions from resolving to active', () => {
      const resolving: SessionLifecycleState = {
        phase: 'resolving',
        workspaceId: 'ws-1',
        agentId: 'agent-a',
      };
      const state = reduceSessionLifecycle(resolving, {
        type: 'SESSION_ACTIVE',
        sessionId: 'sess-1',
        owner: 'legacy-sse',
      });
      expect(state.phase).toBe('active');
      expect(state.sessionId).toBe('sess-1');
      expect(state.owner).toBe('legacy-sse');
    });

    it('preserves mainSessionId when provided', () => {
      const resolving: SessionLifecycleState = {
        phase: 'resolving',
        workspaceId: 'ws-1',
        agentId: 'agent-a',
      };
      const state = reduceSessionLifecycle(resolving, {
        type: 'SESSION_ACTIVE',
        sessionId: 'sess-main',
        owner: 'agent-projection',
        mainSessionId: 'sess-main',
      });
      expect(state.mainSessionId).toBe('sess-main');
    });
  });

  describe('SESSION_TERMINAL', () => {
    it('transitions matching session from active to terminal', () => {
      const active: SessionLifecycleState = {
        phase: 'active',
        workspaceId: 'ws-1',
        agentId: 'agent-a',
        sessionId: 'sess-1',
        owner: 'legacy-sse',
        mainSessionId: 'sess-1',
      };
      const state = reduceSessionLifecycle(active, {
        type: 'SESSION_TERMINAL',
        sessionId: 'sess-1',
        reason: 'deleted',
      });
      expect(state.phase).toBe('terminal');
      expect(state.sessionId).toBeUndefined();
      expect(state.terminalReason).toBe('deleted');
      // mainSessionId 也指向同 session → 应清除
      expect(state.mainSessionId).toBeUndefined();
    });

    it('clears mainSessionId when it matches terminal session', () => {
      const active: SessionLifecycleState = {
        phase: 'active',
        workspaceId: 'ws-1',
        agentId: 'agent-a',
        sessionId: 'sess-main',
        owner: 'legacy-sse',
        mainSessionId: 'sess-main',
      };
      const state = reduceSessionLifecycle(active, {
        type: 'SESSION_TERMINAL',
        sessionId: 'sess-main',
        reason: 'archived',
      });
      expect(state.mainSessionId).toBeUndefined();
    });

    it('preserves mainSessionId when it does NOT match terminal session', () => {
      const active: SessionLifecycleState = {
        phase: 'active',
        workspaceId: 'ws-1',
        agentId: 'agent-a',
        sessionId: 'sess-selected',
        owner: 'legacy-sse',
        mainSessionId: 'sess-main',
      };
      const state = reduceSessionLifecycle(active, {
        type: 'SESSION_TERMINAL',
        sessionId: 'sess-selected',
        reason: 'not-found',
      });
      expect(state.phase).toBe('terminal');
      expect(state.sessionId).toBeUndefined();
      // mainSessionId 不同 → 应保留
      expect(state.mainSessionId).toBe('sess-main');
    });

    it('does NOT change state when terminal is for non-matching session', () => {
      const active: SessionLifecycleState = {
        phase: 'active',
        workspaceId: 'ws-1',
        agentId: 'agent-a',
        sessionId: 'sess-a',
        owner: 'legacy-sse',
      };
      const state = reduceSessionLifecycle(active, {
        type: 'SESSION_TERMINAL',
        sessionId: 'sess-b', // different session
        reason: 'deleted',
      });
      // State must be identical (reference equality not needed, but values must be same)
      expect(state.phase).toBe('active');
      expect(state.sessionId).toBe('sess-a');
    });
  });

  describe('SESSION_CLEANED', () => {
    it('transitions from terminal back to idle', () => {
      const terminal: SessionLifecycleState = {
        phase: 'terminal',
        workspaceId: 'ws-1',
        agentId: 'agent-a',
        terminalReason: 'deleted',
      };
      const state = reduceSessionLifecycle(terminal, {
        type: 'SESSION_CLEANED',
        sessionId: 'sess-1',
      });
      expect(state.phase).toBe('idle');
    });
  });
});

describe('canStartLegacySse', () => {
  it('returns true only for legacy-sse owner in active phase', () => {
    const state: SessionLifecycleState = {
      phase: 'active',
      workspaceId: 'ws-1',
      agentId: 'agent-a',
      sessionId: 'sess-1',
      owner: 'legacy-sse',
    };
    expect(canStartLegacySse(state)).toBe(true);
  });

  it('returns false for agent-projection owner', () => {
    const state: SessionLifecycleState = {
      phase: 'active',
      workspaceId: 'ws-1',
      agentId: 'agent-a',
      sessionId: 'sess-1',
      owner: 'agent-projection',
    };
    expect(canStartLegacySse(state)).toBe(false);
  });

  it('returns false for non-active phase', () => {
    const state: SessionLifecycleState = {
      phase: 'idle',
      workspaceId: 'ws-1',
      agentId: 'agent-a',
      owner: 'legacy-sse',
    };
    expect(canStartLegacySse(state)).toBe(false);
  });
});

describe('canLoadAgentProjection', () => {
  it('returns true only for agent-projection owner in active phase', () => {
    const state: SessionLifecycleState = {
      phase: 'active',
      workspaceId: 'ws-1',
      agentId: 'agent-a',
      sessionId: 'sess-1',
      owner: 'agent-projection',
    };
    expect(canLoadAgentProjection(state)).toBe(true);
  });

  it('returns false for legacy-sse owner', () => {
    const state: SessionLifecycleState = {
      phase: 'active',
      workspaceId: 'ws-1',
      agentId: 'agent-a',
      sessionId: 'sess-1',
      owner: 'legacy-sse',
    };
    expect(canLoadAgentProjection(state)).toBe(false);
  });
});

describe('mapReasonToTerminalReason', () => {
  it('maps delete to deleted', () => {
    expect(mapReasonToTerminalReason('delete')).toBe('deleted');
  });

  it('maps archive to archived', () => {
    expect(mapReasonToTerminalReason('archive')).toBe('archived');
  });

  it('maps sse-404 to not-found', () => {
    expect(mapReasonToTerminalReason('sse-404')).toBe('not-found');
  });

  it('maps replay-poll-404 to not-found', () => {
    expect(mapReasonToTerminalReason('replay-poll-404')).toBe('not-found');
  });

  it('maps sse-410 to gone', () => {
    expect(mapReasonToTerminalReason('sse-410')).toBe('gone');
  });

  it('maps unknown reason to not-found', () => {
    expect(mapReasonToTerminalReason('unknown')).toBe('not-found');
  });
});

describe('createSessionTerminalEvent', () => {
  it('creates SESSION_TERMINAL event from delete reason', () => {
    const event = createSessionTerminalEvent('sess-1', 'delete');
    expect(event.type).toBe('SESSION_TERMINAL');
    if (event.type === 'SESSION_TERMINAL') {
      expect(event.sessionId).toBe('sess-1');
      expect(event.reason).toBe('deleted');
    }
  });

  it('creates SESSION_TERMINAL event from sse-410 reason', () => {
    const event = createSessionTerminalEvent('sess-2', 'sse-410');
    expect(event.type).toBe('SESSION_TERMINAL');
    if (event.type === 'SESSION_TERMINAL') {
      expect(event.sessionId).toBe('sess-2');
      expect(event.reason).toBe('gone');
    }
  });
});
