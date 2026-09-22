import {
  buildMessageFingerprint,
  compareMessageCompleteness,
  detectUnconsumedProjectionEvidence,
  isConversationEquivalent,
  mergeCanonicalConversation,
  normalizeCanonicalText,
  resolveMessageIdentity,
} from './canonicalMerge';
import type {
  AgentConversationView,
  ConversationMessageView,
  ProcessSummaryItem,
} from './types';

const processItem = (id: string): ProcessSummaryItem => ({
  id,
  kind: 'tool',
  status: 'succeeded',
  text: `${id}-output`,
  timestamp: '2026-06-07T00:00:01.000Z',
  sequence: 1,
});

const userMessage = (
  overrides: Partial<ConversationMessageView> = {},
): ConversationMessageView => ({
  messageId: 'user-1',
  role: 'user',
  sourceId: 'admin',
  sourceName: 'Pudding Admin',
  createdAt: '2026-06-07T00:00:00.000Z',
  content: 'long task',
  status: 'sent',
  processItems: [],
  ...overrides,
});

const agentMessage = (
  overrides: Partial<ConversationMessageView> = {},
): ConversationMessageView => ({
  messageId: 'agent-1',
  role: 'agent',
  sourceId: 'agent-a',
  sourceName: 'Agent A',
  createdAt: '2026-06-07T00:00:01.000Z',
  content: 'answer',
  status: 'succeeded',
  processItems: [],
  ...overrides,
});

const conversation = (
  overrides: Partial<AgentConversationView> = {},
): AgentConversationView => ({
  workspaceId: 'default',
  ownerUserId: 'single-user',
  agentId: 'agent-a',
  mainSessionId: 'session-a',
  messages: [userMessage(), agentMessage()],
  activeRun: null,
  eventCursor: 12,
  updatedAt: '2026-06-07T00:00:01.000Z',
  ...overrides,
});

const diagnosticCodes = (
  result: ReturnType<typeof mergeCanonicalConversation>,
): string[] => result.diagnostics.map((diagnostic) => diagnostic.code);

describe('canonical merge（切片 1 纯函数）', () => {
  it('① 同一 messageId、条数不变、正文变长 ⇒ 采纳更长正文（「晚一条」最小复现）', () => {
    const current = conversation({
      messages: [userMessage(), agentMessage({ content: 'partial' })],
    });
    const candidate = conversation({
      messages: [
        userMessage(),
        agentMessage({ content: 'partial + materialized tail' }),
      ],
    });

    // 旧判据（cursor/条数/activeRun/mainSessionId）会认为两者相同。
    expect(current.messages.length).toBe(candidate.messages.length);
    expect(current.eventCursor).toBe(candidate.eventCursor);
    expect(isConversationEquivalent(current, candidate)).toBe(false);

    const result = mergeCanonicalConversation(current, candidate);

    expect(result.changed).toBe(true);
    expect(result.conversation.messages[1].content).toBe(
      'partial + materialized tail',
    );
    expect(diagnosticCodes(result)).toEqual(['content-conflict-kept-longer']);
    expect(result.diagnostics[0].messageId).toBe('id:agent-1');
    expect(result.diagnostics[0].kept?.contentLength ?? 0).toBeGreaterThan(
      result.diagnostics[0].discarded?.contentLength ?? 0,
    );
  });

  it('② 同游标、同 messageId、正文不同 ⇒ 保留较长者并产生冲突诊断', () => {
    const current = conversation({
      messages: [agentMessage({ content: 'short body' })],
    });
    const candidate = conversation({
      messages: [agentMessage({ content: 'a much longer body' })],
    });

    const result = mergeCanonicalConversation(current, candidate);

    expect(result.conversation.messages[0].content).toBe(
      'a much longer body',
    );
    expect(result.diagnostics).toHaveLength(1);
    expect(result.diagnostics[0]).toMatchObject({
      code: 'content-conflict-kept-longer',
      messageId: 'id:agent-1',
      currentCursor: 12,
      candidateCursor: 12,
    });

    // 反向：较轻候选不得压掉较重存量，且必须是 no-op（无重复诊断 ⇒ 幂等）。
    const shorterCandidate = conversation({
      messages: [agentMessage({ content: 'short' })],
    });
    const reverse = mergeCanonicalConversation(
      result.conversation,
      shorterCandidate,
    );
    expect(reverse.changed).toBe(false);
    expect(reverse.conversation).toBe(result.conversation);
    expect(reverse.diagnostics).toEqual([]);
    expect(reverse.conversation.messages[0].content).toBe(
      'a much longer body',
    );
  });

  it('③ 非空正文不得被空正文覆盖（同游标与更新游标两种情形）', () => {
    const current = conversation({
      messages: [agentMessage({ content: 'complete answer' })],
    });

    const sameCursorEmpty = mergeCanonicalConversation(
      current,
      conversation({ messages: [agentMessage({ content: '' })] }),
    );
    expect(sameCursorEmpty.changed).toBe(false);
    expect(sameCursorEmpty.conversation.messages[0].content).toBe(
      'complete answer',
    );

    const newerCursorEmpty = mergeCanonicalConversation(
      current,
      conversation({
        eventCursor: 13,
        messages: [agentMessage({ content: '   ', status: 'succeeded' })],
      }),
    );
    expect(newerCursorEmpty.conversation.messages[0].content).toBe(
      'complete answer',
    );
    expect(diagnosticCodes(newerCursorEmpty)).toContain(
      'content-rollback-rejected',
    );
  });

  it('④ 条数相同但 messageId/正文不同 ⇒ 不得判为等价', () => {
    const current = conversation();
    const differentIdentity = conversation({
      messages: [userMessage(), agentMessage({ messageId: 'agent-2' })],
    });
    const differentBody = conversation({
      messages: [userMessage(), agentMessage({ content: 'other answer' })],
    });
    const differentStatus = conversation({
      messages: [userMessage(), agentMessage({ status: 'streaming' })],
    });

    expect(
      current.messages.length === differentIdentity.messages.length,
    ).toBe(true);
    expect(isConversationEquivalent(current, differentIdentity)).toBe(false);
    expect(isConversationEquivalent(current, differentBody)).toBe(false);
    expect(isConversationEquivalent(current, differentStatus)).toBe(false);

    // 等价仍然可判定：完全相同的快照必须判为等价（否则性能短路永远失效）。
    expect(isConversationEquivalent(current, conversation())).toBe(true);
  });

  it('⑤ 证据不足（缺身份 / 缺游标）⇒ 不得覆盖已有完整记录', () => {
    const current = conversation({
      messages: [agentMessage({ content: 'complete answer' })],
    });

    const anonymous = {
      role: 'agent',
      sourceId: '',
      sourceName: '',
      createdAt: '',
      content: '',
      status: 'succeeded',
      processItems: [],
    } as unknown as ConversationMessageView;
    expect(resolveMessageIdentity(anonymous).key).toBeNull();

    const identityless = mergeCanonicalConversation(
      current,
      conversation({ messages: [anonymous] }),
    );
    expect(identityless.conversation.messages[0].content).toBe(
      'complete answer',
    );

    const missingCursor = mergeCanonicalConversation(current, {
      ...conversation({ messages: [agentMessage({ content: 'short' })] }),
      eventCursor: undefined as unknown as number,
    });
    expect(missingCursor.changed).toBe(false);
    expect(missingCursor.conversation.messages[0].content).toBe(
      'complete answer',
    );
  });

  it('⑥ 合并幂等：同一候选快照合并两次，结果与诊断都不重复', () => {
    const current = conversation({
      messages: [agentMessage({ content: 'partial' })],
    });
    const candidate = conversation({
      messages: [agentMessage({ content: 'partial and then some' })],
    });

    const first = mergeCanonicalConversation(current, candidate);
    const second = mergeCanonicalConversation(first.conversation, candidate);

    expect(first.changed).toBe(true);
    expect(first.diagnostics).toHaveLength(1);
    expect(second.changed).toBe(false);
    expect(second.diagnostics).toEqual([]);
    expect(second.conversation).toBe(first.conversation);
    expect(second.conversation.messages).toHaveLength(
      first.conversation.messages.length,
    );
  });

  it('不 mutate 入参（返回新对象）', () => {
    const current = conversation({
      messages: [agentMessage({ content: 'partial' })],
    });
    const candidate = conversation({
      messages: [agentMessage({ content: 'partial and then some' })],
    });
    const before = JSON.stringify([current, candidate]);

    const result = mergeCanonicalConversation(current, candidate);

    expect(JSON.stringify([current, candidate])).toBe(before);
    expect(result.conversation).not.toBe(current);
    expect(result.conversation.messages).not.toBe(current.messages);
    expect(result.conversation.messages[0]).not.toBe(current.messages[0]);
  });

  it('归一化只做行尾归一与首尾裁剪，不做语义改写', () => {
    expect(normalizeCanonicalText('a\r\nb ')).toBe('a\nb');
    expect(normalizeCanonicalText('  a\rb  ')).toBe('a\nb');
    expect(normalizeCanonicalText(null)).toBe('');
    expect(
      buildMessageFingerprint(agentMessage({ content: 'x\r\ny ' })).contentHash,
    ).toBe(buildMessageFingerprint(agentMessage({ content: 'x\ny' })).contentHash);
    expect(
      buildMessageFingerprint(agentMessage({ content: 'x' })).contentHash,
    ).not.toBe(
      buildMessageFingerprint(agentMessage({ content: 'y' })).contentHash,
    );
  });

  it('过程块数不得减少、游标不得回退', () => {
    const current = conversation({
      messages: [
        agentMessage({
          content: 'answer',
          processItems: [processItem('t1'), processItem('t2'), processItem('t3')],
        }),
      ],
    });
    const candidate = conversation({
      eventCursor: 13,
      messages: [
        agentMessage({
          content: 'answer with a longer final body',
          processItems: [processItem('t1')],
        }),
      ],
    });

    const result = mergeCanonicalConversation(current, candidate);
    expect(result.conversation.messages[0].processItems).toHaveLength(3);
    expect(result.conversation.eventCursor).toBe(13);
    expect(diagnosticCodes(result)).toContain(
      'process-items-rollback-rejected',
    );

    const stale = conversation({
      eventCursor: 5,
      messages: [agentMessage({ content: 'stale body' })],
    });
    const mergedStale = mergeCanonicalConversation(result.conversation, stale);
    expect(mergedStale.conversation.eventCursor).toBe(13);
    expect(mergedStale.conversation.messages[0].content).toBe(
      'answer with a longer final body',
    );
  });

  it('同游标完全平局时采纳候选（服务端最新响应）并记录诊断', () => {
    const current = conversation({
      messages: [agentMessage({ content: 'abcd' })],
    });
    const candidate = conversation({
      messages: [agentMessage({ content: 'abce' })],
    });

    expect(
      compareMessageCompleteness(
        buildMessageFingerprint(candidate.messages[0]),
        buildMessageFingerprint(current.messages[0]),
      ),
    ).toBe(0);

    const result = mergeCanonicalConversation(current, candidate);
    expect(result.changed).toBe(true);
    expect(result.conversation.messages[0].content).toBe('abce');
    expect(diagnosticCodes(result)).toEqual([
      'fingerprint-conflict-adopted-latest',
    ]);
  });

  it('同一快照内重复身份只保留首次实体', () => {
    const duplicated = conversation({
      messages: [
        agentMessage({ content: 'first entity' }),
        agentMessage({ content: 'duplicate entity' }),
      ],
    });
    const result = mergeCanonicalConversation(null, duplicated);
    expect(result.conversation).toBe(duplicated);

    const current = conversation({
      messages: [agentMessage({ content: 'existing' })],
    });
    const deduped = mergeCanonicalConversation(current, duplicated);
    expect(deduped.changed).toBe(true);
    expect(deduped.conversation.messages).toHaveLength(1);
    expect(deduped.conversation.messages[0].content).toBe('first entity');
  });

  it('scope 不同不合并；mainSessionId 轮换采纳候选', () => {
    const current = conversation();
    const otherScope = conversation({ agentId: 'agent-z', messages: [] });
    const mismatch = mergeCanonicalConversation(current, otherScope);
    expect(mismatch.changed).toBe(false);
    expect(mismatch.conversation).toBe(current);
    expect(diagnosticCodes(mismatch)).toEqual(['scope-mismatch']);

    const rotated = conversation({
      mainSessionId: 'session-b',
      messages: [userMessage()],
    });
    const sessionResult = mergeCanonicalConversation(current, rotated);
    expect(sessionResult.changed).toBe(true);
    expect(sessionResult.conversation).toBe(rotated);
    expect(diagnosticCodes(sessionResult)).toEqual(['session-rotated']);
  });

  it('known 字段不被 undefined 覆盖', () => {
    const current = conversation({
      messages: [agentMessage({ content: 'answer', turnId: 'turn-1' })],
    });
    const candidate = conversation({
      eventCursor: 13,
      messages: [
        agentMessage({
          content: 'answer plus more',
          turnId: undefined,
        }),
      ],
    });

    const result = mergeCanonicalConversation(current, candidate);
    expect(result.conversation.messages[0].content).toBe('answer plus more');
    expect(result.conversation.messages[0].turnId).toBe('turn-1');
  });
});

describe('projection 证据判定（切片 2 判据）', () => {
  it('识别未消费的待物化 / 待追平证据', () => {
    expect(detectUnconsumedProjectionEvidence(null)).toEqual([]);
    expect(
      detectUnconsumedProjectionEvidence(
        conversation({ messages: [], eventCursor: 12 }),
      ).map((evidence) => evidence.code),
    ).toEqual(['conversation-empty-with-cursor']);
    expect(
      detectUnconsumedProjectionEvidence(
        conversation({ messages: [userMessage()] }),
      ).map((evidence) => evidence.code),
    ).toEqual(['user-message-awaiting-reply']);
    expect(
      detectUnconsumedProjectionEvidence(
        conversation({ messages: [userMessage(), agentMessage({ content: '' })] }),
      ).map((evidence) => evidence.code),
    ).toEqual(['message-body-not-materialized']);
    expect(
      detectUnconsumedProjectionEvidence(
        conversation({
          messages: [
            userMessage(),
            agentMessage({ content: 'partial', status: 'streaming' }),
          ],
        }),
      ).map((evidence) => evidence.code),
    ).toEqual(['message-status-not-terminal']);
  });

  it('已物化的终态快照与空快照都不算未追平', () => {
    expect(
      detectUnconsumedProjectionEvidence(conversation()),
    ).toEqual([]);
    expect(
      detectUnconsumedProjectionEvidence(
        conversation({ messages: [], eventCursor: 0 }),
      ),
    ).toEqual([]);
  });

  it('activeRun 与空游标都是证据', () => {
    const running = detectUnconsumedProjectionEvidence(
      conversation({
        messages: [userMessage(), agentMessage()],
        activeRun: {
          runId: 'run-1',
          workspaceId: 'default',
          ownerUserId: 'single-user',
          agentId: 'agent-a',
          mainSessionId: 'session-a',
          status: 'running',
          statusText: 'running',
          summary: '',
          eventCursor: 13,
          outputSnapshot: { markdown: '', processItems: [] },
          startedAt: '2026-06-07T00:00:00.000Z',
          updatedAt: '2026-06-07T00:00:02.000Z',
        },
      }),
    );
    expect(running.map((evidence) => evidence.code)).toContain(
      'active-run-in-flight',
    );
  });
});
