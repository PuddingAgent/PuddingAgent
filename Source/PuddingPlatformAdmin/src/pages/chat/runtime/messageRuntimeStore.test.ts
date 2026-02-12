// ── Message Runtime Store 单测 ─────────────────────────────────
// ADR-054 Step 4: 验证 delta flush、thinking flush、turn 引用稳定性

import { createMessageRuntimeStore } from './messageRuntimeStore';

describe('MessageRuntimeStore', () => {
  describe('turn management', () => {
    it('appendTurn adds turn to end of list', () => {
      const store = createMessageRuntimeStore();
      const turn = {
        turnId: 't1',
        userMessage: {
          id: 'm1',
          text: 'hello',
          timestamp: 1,
          status: 'success' as const,
        },
        assistant: {
          id: 'm2',
          status: 'success' as const,
          answerMarkdown: 'hi',
          isStreaming: false,
        },
      };
      store.appendTurn(turn);
      expect(store.getSnapshot().turns).toHaveLength(1);
      expect(store.getSnapshot().turns[0].turnId).toBe('t1');
    });

    it('setTurns replaces all turns', () => {
      const store = createMessageRuntimeStore();
      const turns = [
        {
          turnId: 't1',
          userMessage: {
            id: 'm1',
            text: 'a',
            timestamp: 1,
            status: 'success' as const,
          },
          assistant: {
            id: 'm2',
            status: 'success' as const,
            answerMarkdown: 'b',
            isStreaming: false,
          },
        },
      ];
      store.setTurns(turns);
      expect(store.getSnapshot().turns).toHaveLength(1);
    });

    it('prependTurns inserts turns at beginning', () => {
      const store = createMessageRuntimeStore();
      const turn1 = {
        turnId: 't1',
        userMessage: {
          id: 'm1',
          text: 'a',
          timestamp: 1,
          status: 'success' as const,
        },
        assistant: {
          id: 'm2',
          status: 'success' as const,
          answerMarkdown: 'b',
          isStreaming: false,
        },
      };
      const turn2 = {
        turnId: 't2',
        userMessage: {
          id: 'm3',
          text: 'c',
          timestamp: 2,
          status: 'success' as const,
        },
        assistant: {
          id: 'm4',
          status: 'success' as const,
          answerMarkdown: 'd',
          isStreaming: false,
        },
      };
      store.setTurns([turn2]);
      store.prependTurns([turn1]);
      expect(store.getSnapshot().turns).toHaveLength(2);
      expect(store.getSnapshot().turns[0].turnId).toBe('t1');
    });

    it('updateAssistantTurn updates only the target turn', () => {
      const store = createMessageRuntimeStore();
      const turn1 = {
        turnId: 't1',
        userMessage: {
          id: 'm1',
          text: 'a',
          timestamp: 1,
          status: 'success' as const,
        },
        assistant: {
          id: 'm2',
          status: 'streaming' as const,
          answerMarkdown: '...',
          isStreaming: true,
        },
      };
      const turn2 = {
        turnId: 't2',
        userMessage: {
          id: 'm3',
          text: 'c',
          timestamp: 2,
          status: 'success' as const,
        },
        assistant: {
          id: 'm4',
          status: 'success' as const,
          answerMarkdown: 'done',
          isStreaming: false,
        },
      };
      store.setTurns([turn1, turn2]);

      const beforeTurn2 = store.getSnapshot().turns[1];
      store.updateAssistantTurn('t1', {
        status: 'success',
        isStreaming: false,
      });

      const after = store.getSnapshot();
      expect(after.turns[0].assistant.status).toBe('success');
      // turn2 引用应保持不变
      expect(after.turns[1]).toBe(beforeTurn2);
    });
  });

  describe('delta buffer', () => {
    it('enqueueDelta marks messages as active', () => {
      const store = createMessageRuntimeStore();
      store.enqueueDelta('msg-1', 'turn-1', 'Hello');
      const active = store.getSnapshot().activeMessageIds;
      expect(active.has('msg-1')).toBe(true);
    });

    it('flushPendingDeltas applies deltas to matching turns', (done) => {
      // 使用真实 timers 因为 flush 依赖 setTimeout
      jest.useRealTimers();
      const store = createMessageRuntimeStore();
      const turn = {
        turnId: 't1',
        userMessage: {
          id: 'm1',
          text: 'q',
          timestamp: 1,
          status: 'success' as const,
        },
        assistant: {
          id: 'msg-1',
          status: 'streaming' as const,
          answerMarkdown: '',
          isStreaming: true,
        },
      };
      store.setTurns([turn]);
      store.enqueueDelta('msg-1', 't1', 'Hello');

      // 等待 flush
      setTimeout(() => {
        store.flushPendingDeltas();
        const snap = store.getSnapshot();
        expect(snap.turns[0].assistant.answerMarkdown).toBe('Hello');
        done();
      }, 100);
    });
  });

  describe('subAgentCards', () => {
    it('upsertSubAgentCard adds new card', () => {
      const store = createMessageRuntimeStore();
      store.upsertSubAgentCard({
        subAgentId: 'sub-1',
        parentMessageId: 'msg-1',
        name: 'SubAgent A',
        status: 'running',
        createdAt: Date.now(),
      });
      expect(store.getSnapshot().subAgentCards['sub-1']).toBeDefined();
    });

    it('upsertSubAgentCard updates existing card without replacing other turns', () => {
      const store = createMessageRuntimeStore();
      const turn = {
        turnId: 't1',
        userMessage: {
          id: 'm1',
          text: 'q',
          timestamp: 1,
          status: 'success' as const,
        },
        assistant: {
          id: 'm2',
          status: 'success' as const,
          answerMarkdown: 'a',
          isStreaming: false,
        },
      };
      store.setTurns([turn]);

      store.upsertSubAgentCard({
        subAgentId: 'sub-1',
        parentMessageId: 'msg-1',
        name: 'Sub',
        status: 'running',
        createdAt: 1,
      });

      store.upsertSubAgentCard({
        subAgentId: 'sub-1',
        parentMessageId: 'msg-1',
        name: 'Sub',
        status: 'completed',
        summary: 'Done',
        createdAt: 1,
      });

      expect(store.getSnapshot().subAgentCards['sub-1'].status).toBe(
        'completed',
      );
      // turns 不应被替换
      expect(store.getSnapshot().turns).toHaveLength(1);
    });
  });

  describe('clear', () => {
    it('clears all state', () => {
      const store = createMessageRuntimeStore();
      store.appendTurn({
        turnId: 't1',
        userMessage: {
          id: 'm1',
          text: 'q',
          timestamp: 1,
          status: 'success' as const,
        },
        assistant: {
          id: 'm2',
          status: 'success' as const,
          answerMarkdown: 'a',
          isStreaming: false,
        },
      });
      store.upsertSubAgentCard({
        subAgentId: 's1',
        parentMessageId: 'm1',
        name: 'S',
        status: 'running',
        createdAt: 1,
      });
      store.clear();

      const snap = store.getSnapshot();
      expect(snap.turns).toHaveLength(0);
      expect(Object.keys(snap.subAgentCards)).toHaveLength(0);
      expect(snap.activeMessageIds.size).toBe(0);
    });
  });

  describe('subscribe', () => {
    it('notifies listeners on mutation', () => {
      const store = createMessageRuntimeStore();
      const listener = jest.fn();
      store.subscribe(listener);

      store.appendTurn({
        turnId: 't1',
        userMessage: {
          id: 'm1',
          text: 'q',
          timestamp: 1,
          status: 'success' as const,
        },
        assistant: {
          id: 'm2',
          status: 'success' as const,
          answerMarkdown: 'a',
          isStreaming: false,
        },
      });
      expect(listener).toHaveBeenCalledTimes(1);
    });
  });
});
