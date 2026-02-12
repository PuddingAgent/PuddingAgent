import type {
  WorkspaceAgentDto,
  WorkspaceWithPermDto,
} from '@/services/platform/api';
import type { ChatTurn } from '../types';
import { getChatRouteLabel, resolveChatRoute } from './chatRouting';
import {
  applyBufferedDeltaToTurn,
  buildAgentMainSessionRequest,
  buildChatMessageRequest,
  buildSessionEventReplayUrl,
  canBindUnknownMetadataToTurn,
  filterSubAgentCardsForSession,
  formatChatErrorDiagnostic,
  formatCompactSuccessMessage,
  getChatRouteSelectionFromSearch,
  getHistoryReconcileBlockReason,
  getTrackedActiveMessageIds,
  hasBlockingActiveTurn,
  hasTrackedActiveSessionMessages,
  parseSessionEventTimestampMs,
  removeInjectedSteeringQueueItem,
  resolveActiveSessionReplayFromSequence,
  resolveInitialAgentId,
  resolveInitialWorkspaceId,
  resolveSessionReplayCursorSequence,
  resolveSessionReplayPollInterval,
  resolveSubAgentTaskSummary,
  resolveSubAgentTerminalOutput,
  resolveTerminalAssistantMarkdown,
  resolveTurnIdForEvent,
  shouldAdvanceSequenceForSessionEvent,
  isChatStreamErrorEvent,
  looksLikePersistedErrorDiagnostic,
  shouldHydrateSessionEventReplay,
  shouldReplayEventsAfterHistory,
  shouldResetSequenceForSessionChange,
  shouldRunSessionReplayCompensation,
  toChatInteractionRuntimeEvent,
  toSessionListItem,
} from './useChatState';

const turn = (answerMarkdown: string): ChatTurn => ({
  turnId: `turn-${answerMarkdown || 'empty'}`,
  userMessage: {
    id: 'user-1',
    text: '如何进行评估',
    timestamp: 1,
    status: 'success',
  },
  assistant: {
    id: 'assistant-1',
    status: answerMarkdown ? 'success' : 'thinking',
    timelineItems: [],
    answerMarkdown,
    isStreaming: !answerMarkdown,
    renderMode: 'structured',
  },
});

describe('chat session recovery decisions', () => {
  const workspace = ({
    workspaceId,
    ...overrides
  }: Partial<WorkspaceWithPermDto> &
    Pick<WorkspaceWithPermDto, 'workspaceId'>): WorkspaceWithPermDto => ({
    id: 1,
    workspaceId,
    slug: workspaceId,
    teamId: 'team-1',
    teamName: '默认团队',
    name: workspaceId,
    isEnabled: true,
    isFrozen: false,
    memberCount: 1,
    teamAccessPolicy: 'Write',
    companyAccessPolicy: 'None',
    createdAt: '2026-05-25T00:00:00Z',
    ...overrides,
  });

  const agentDto = (
    overrides: Partial<WorkspaceAgentDto> &
      Pick<WorkspaceAgentDto, 'agentId' | 'name'>,
  ): WorkspaceAgentDto => ({
    displayName: overrides.name,
    isEnabled: true,
    isFrozen: false,
    createdAt: '2026-05-25T00:00:00Z',
    updatedAt: '2026-05-25T00:00:00Z',
    ...overrides,
  });

  it('reads workspace and agent selection from chat route query', () => {
    expect(
      getChatRouteSelectionFromSearch(
        '?workspaceId=default&agentId=assistant-1',
      ),
    ).toEqual({
      workspaceId: 'default',
      agentId: 'assistant-1',
    });
    expect(
      getChatRouteSelectionFromSearch(
        '?workspaceId=default&agentId=assistant-1&sessionId=session-1',
      ),
    ).toEqual({
      workspaceId: 'default',
      agentId: 'assistant-1',
      sessionId: 'session-1',
    });
    expect(
      getChatRouteSelectionFromSearch('?workspaceId=%20&agentId='),
    ).toEqual({});
  });

  it('prefers route-selected workspace and agent when they exist', () => {
    expect(
      resolveInitialWorkspaceId(
        [
          workspace({ workspaceId: 'default' }),
          workspace({ workspaceId: 'studio' }),
        ],
        'studio',
      ),
    ).toBe('studio');

    expect(
      resolveInitialAgentId(
        [
          agentDto({ agentId: 'assistant', name: '助手' }),
          agentDto({ agentId: 'reviewer', name: '审查' }),
        ],
        'reviewer',
      ),
    ).toBe('reviewer');
  });

  it('falls back to enabled workspace and agent when route selection is stale', () => {
    expect(
      resolveInitialWorkspaceId(
        [
          workspace({ workspaceId: 'frozen', isFrozen: true }),
          workspace({ workspaceId: 'active' }),
        ],
        'missing',
      ),
    ).toBe('active');

    expect(
      resolveInitialAgentId(
        [
          agentDto({ agentId: 'frozen', name: '冻结', isFrozen: true }),
          agentDto({ agentId: 'active', name: '可用' }),
        ],
        'missing',
      ),
    ).toBe('active');
  });

  it('builds a workspace agent main-session request', () => {
    expect(
      buildAgentMainSessionRequest(
        'default',
        agentDto({
          agentId: 'assistant',
          name: '默认助手',
          displayName: '布丁',
          sourceTemplateId: 'global:general-assistant',
        }),
      ),
    ).toEqual({
      workspaceId: 'default',
      principalKind: 'agent',
      principalId: 'assistant',
      agentTemplateId: 'global:general-assistant',
      title: '布丁',
    });

    expect(
      buildAgentMainSessionRequest(
        'default',
        agentDto({
          agentId: 'reviewer',
          name: '审查助手',
        }),
      ),
    ).toEqual({
      workspaceId: 'default',
      principalKind: 'agent',
      principalId: 'reviewer',
      agentTemplateId: 'global:reviewer',
      title: '审查助手',
    });
  });

  it('preserves server session metadata when mapping sessions for agent switching', () => {
    expect(
      toSessionListItem({
        sessionId: 'legacy-session',
        workspaceId: 'default',
        agentTemplateId: 'global:general-assistant',
        channelId: 'admin-chat',
        ownerUserId: 'admin',
        sessionType: 'ServiceSession',
        sessionRole: 'Task',
        status: 'Idle',
        title: '你好',
        createdAt: '2026-06-06T10:00:00+08:00',
        lastActiveAt: '2026-06-06T10:30:00+08:00',
      }),
    ).toMatchObject({
      sessionId: 'legacy-session',
      title: '你好',
      agentTemplateId: 'global:general-assistant',
      channelId: 'admin-chat',
      sessionRole: 'Task',
    });
  });

  it('does not advance the event sequence when a turn-scoped event has no target turn yet', () => {
    expect(shouldAdvanceSequenceForSessionEvent('delta', false)).toBe(false);
    expect(shouldAdvanceSequenceForSessionEvent('thinking', false)).toBe(false);
    expect(shouldAdvanceSequenceForSessionEvent('done', false)).toBe(false);
  });

  it('advances the event sequence for global realtime interaction status events', () => {
    expect(
      shouldAdvanceSequenceForSessionEvent('voice_capture_status', false),
    ).toBe(true);
    expect(
      shouldAdvanceSequenceForSessionEvent('camera_capture_status', false),
    ).toBe(true);
    expect(
      shouldAdvanceSequenceForSessionEvent('visual_reasoning_status', false),
    ).toBe(true);
  });

  it('does not let terminal session events skip replay before the target turn exists', () => {
    expect(shouldAdvanceSequenceForSessionEvent('session.closed', false)).toBe(
      false,
    );
    expect(shouldAdvanceSequenceForSessionEvent('session.closed', true)).toBe(
      true,
    );
  });

  it('replays event history after loading a session whose latest assistant answer is still empty', () => {
    expect(shouldReplayEventsAfterHistory([turn('')])).toBe(true);
    expect(shouldReplayEventsAfterHistory([turn('完整回答')])).toBe(false);
    expect(shouldReplayEventsAfterHistory([])).toBe(true);
  });

  it('does not reconcile stale history over the currently streaming turn', () => {
    const current = [
      {
        ...turn('旧回答'),
        turnId: 'turn-old',
        userMessage: {
          ...turn('旧回答').userMessage,
          text: '问题1',
          timestamp: 1,
        },
      },
      {
        ...turn(''),
        turnId: 'turn-current',
        userMessage: {
          ...turn('').userMessage,
          text: '问题2',
          timestamp: 10_000,
        },
      },
    ];
    const staleHistory = [
      {
        ...turn('旧回答'),
        turnId: 'hist-old',
        userMessage: {
          ...turn('旧回答').userMessage,
          text: '问题1',
          timestamp: 1,
        },
      },
    ];

    expect(getHistoryReconcileBlockReason(current, staleHistory)).toBe(
      'active-turn-not-materialized',
    );
  });

  it('allows reconcile when history contains the latest user turn', () => {
    const current = [
      {
        ...turn(''),
        turnId: 'turn-current',
        userMessage: {
          ...turn('').userMessage,
          text: '问题2',
          timestamp: 10_000,
        },
      },
    ];
    const freshHistory = [
      {
        ...turn('新回答'),
        turnId: 'hist-current',
        userMessage: {
          ...turn('新回答').userMessage,
          text: '问题2',
          timestamp: 11_000,
        },
      },
    ];

    expect(getHistoryReconcileBlockReason(current, freshHistory)).toBeNull();
  });

  it('hydrates completed event replays instead of visually streaming them again', () => {
    expect(
      shouldHydrateSessionEventReplay([{ type: 'delta' }, { type: 'done' }]),
    ).toBe(true);
    expect(
      shouldHydrateSessionEventReplay([
        { type: 'delta' },
        { type: 'thinking' },
      ]),
    ).toBe(false);
  });

  it('keeps accumulated streaming text when done reply only contains the post-tool tail', () => {
    const beforeTool = '# 商用密码技术\n\n这里是前半段完整说明。';
    const afterTool = '**当前工作目录下没有找到任何 PDF 文件**。';
    const accumulated = beforeTool + '\n\n' + afterTool;

    expect(resolveTerminalAssistantMarkdown(accumulated, afterTool)).toBe(
      accumulated,
    );
  });

  it('uses terminal reply when no streaming text has been accumulated', () => {
    expect(resolveTerminalAssistantMarkdown('', '最终回答')).toBe('最终回答');
  });

  it('uses the complete terminal reply when accumulated text is a suffix of it', () => {
    const full = '# 商用密码算法简介\n\n前半段说明。\n\n后半段结论。';
    expect(resolveTerminalAssistantMarkdown(full.slice(8), full)).toBe(full);
  });

  it('deduplicates overlapped terminal reply instead of appending the whole answer twice', () => {
    const before = '# 商用密码算法简介\n\n前半段说明。';
    const after = '\n\n后半段结论。';
    expect(
      resolveTerminalAssistantMarkdown(before + after.slice(0, 4), after),
    ).toBe(before + after);
  });

  it('keeps buffered deltas even when the terminal event has already completed the turn', () => {
    const completed = {
      ...turn(''),
      assistant: {
        ...turn('').assistant,
        status: 'success' as const,
        isStreaming: false,
      },
    };

    const next = applyBufferedDeltaToTurn(completed, '终端前已接收的内容');
    expect(next.assistant.answerMarkdown).toBe('终端前已接收的内容');
    expect(next.assistant.status).toBe('success');
    expect(next.assistant.isStreaming).toBe(false);
  });

  it('resets the event cursor when streaming switches to a different session', () => {
    expect(shouldResetSequenceForSessionChange('session-a', 'session-b')).toBe(
      true,
    );
    expect(shouldResetSequenceForSessionChange('session-a', 'session-a')).toBe(
      false,
    );
    expect(shouldResetSequenceForSessionChange(null, 'session-b')).toBe(false);
  });

  it('uses the forward replay endpoint for missed session events', () => {
    expect(buildSessionEventReplayUrl('session/a b', 42, 100)).toBe(
      '/api/sessions/session%2Fa%20b/replay?from=42&limit=100',
    );
  });

  it('backs up one replay page while a turn is active so missed terminal events can close it', () => {
    expect(resolveActiveSessionReplayFromSequence(62, 50)).toBe(13);
    expect(resolveActiveSessionReplayFromSequence(8, 50)).toBe(1);
    expect(resolveActiveSessionReplayFromSequence(Number.NaN, 50)).toBe(1);
  });

  it('uses replay total count as the completed history cursor', () => {
    expect(resolveSessionReplayCursorSequence({ totalEventCount: 377 })).toBe(
      377,
    );
    expect(resolveSessionReplayCursorSequence({ TotalEventCount: 12 })).toBe(
      12,
    );
  });

  it('falls back to the max event sequence when replay total count is missing', () => {
    expect(
      resolveSessionReplayCursorSequence({
        events: [{ sequenceNum: 3 }, { sequenceNum: 7 }, { SequenceNum: 5 }],
      }),
    ).toBe(7);
    expect(resolveSessionReplayCursorSequence({ events: [] })).toBeNull();
  });

  it('reads replay cursor sequence from nested event data json', () => {
    expect(
      resolveSessionReplayCursorSequence({
        events: [
          {
            eventType: 'thinking',
            data: '{"sequenceNum":32,"messageId":"m-1"}',
          },
          { EventType: 'delta', Data: '{"SequenceNum":81,"messageId":"m-1"}' },
        ],
      }),
    ).toBe(81);
  });

  it('uses short replay compensation while messages are actively streaming', () => {
    expect(resolveSessionReplayPollInterval(true)).toBe(900);
    expect(resolveSessionReplayPollInterval(false)).toBe(8000);
  });

  it('only treats tracked active message ids as active replay work', () => {
    const staleActiveTurn = turn('');
    const completedTrackedTurn = {
      ...turn('完整回答'),
      turnId: 'completed-turn',
    };

    expect(
      hasTrackedActiveSessionMessages([], new Map(), [staleActiveTurn]),
    ).toBe(false);

    expect(
      hasTrackedActiveSessionMessages(
        ['message-1'],
        new Map([['message-1', 'completed-turn']]),
        [completedTrackedTurn],
      ),
    ).toBe(false);

    expect(
      hasTrackedActiveSessionMessages(
        ['message-2'],
        new Map([['message-2', staleActiveTurn.turnId]]),
        [staleActiveTurn],
      ),
    ).toBe(true);
  });

  it('blocks a new send while any assistant turn is still active', () => {
    const activeTurn = turn('');
    const completedTurn = { ...turn('完成'), turnId: 'completed-turn' };

    expect(hasBlockingActiveTurn([completedTurn], [], new Map())).toBe(false);

    expect(hasBlockingActiveTurn([activeTurn], [], new Map())).toBe(true);

    expect(
      hasBlockingActiveTurn(
        [completedTurn],
        ['active-message'],
        new Map([['active-message', completedTurn.turnId]]),
      ),
    ).toBe(false);
  });

  it('prunes tracked active message ids that no longer point at active turns', () => {
    const activeTurn = turn('');
    const completedTurn = { ...turn('完成'), turnId: 'completed-turn' };

    expect([
      ...getTrackedActiveMessageIds(
        ['active-message', 'completed-message', 'missing-message'],
        new Map([
          ['active-message', activeTurn.turnId],
          ['completed-message', completedTurn.turnId],
        ]),
        [activeTurn, completedTurn],
      ),
    ]).toEqual(['active-message']);
  });

  it('skips replay compensation while active SSE has recent events', () => {
    expect(
      shouldRunSessionReplayCompensation({
        hasActiveMessages: true,
        lastSseEventAt: 1000,
        now: 3200,
      }),
    ).toBe(false);
    expect(
      shouldRunSessionReplayCompensation({
        hasActiveMessages: true,
        lastSseEventAt: 1000,
        now: 3600,
      }),
    ).toBe(true);
    expect(
      shouldRunSessionReplayCompensation({
        hasActiveMessages: true,
        lastSseEventAt: null,
        now: 1200,
      }),
    ).toBe(true);
    expect(
      shouldRunSessionReplayCompensation({
        hasActiveMessages: false,
        lastSseEventAt: 1200,
        now: 1300,
      }),
    ).toBe(false);
  });

  it('does not bind unknown message-scoped events to the latest turn', () => {
    const map = new Map([['message-1', 'turn-1']]);

    expect(
      resolveTurnIdForEvent(
        { type: 'delta', messageId: 'message-2' },
        map,
        'turn-latest',
      ),
    ).toBeNull();
    expect(
      resolveTurnIdForEvent(
        { type: 'delta', messageId: 'message-1' },
        map,
        'turn-latest',
      ),
    ).toBe('turn-1');
    expect(
      resolveTurnIdForEvent(
        { type: 'metadata', messageId: 'message-2' },
        map,
        'turn-latest',
        false,
      ),
    ).toBeNull();
    expect(
      resolveTurnIdForEvent(
        { type: 'metadata', messageId: 'message-2' },
        map,
        'turn-latest',
        true,
      ),
    ).toBe('turn-latest');
    expect(
      resolveTurnIdForEvent(
        { type: 'metadata', messageId: 'message-3', fanout_index: '1' },
        map,
        'turn-latest',
      ),
    ).toBeNull();
  });

  it('only binds unknown metadata to the latest turn while that turn is still active', () => {
    expect(canBindUnknownMetadataToTurn(turn(''))).toBe(true);
    expect(
      canBindUnknownMetadataToTurn({
        ...turn('完整回答'),
        assistant: {
          ...turn('完整回答').assistant,
          status: 'success',
          isStreaming: false,
        },
      }),
    ).toBe(false);
  });

  it('only exposes sub-agent cards that belong to the selected session', () => {
    const cards = filterSubAgentCardsForSession(
      {
        'sa-session-a-sub-11111111': {
          turnId: 'sa-session-a-sub-11111111',
          subSessionId: 'session-a-sub-11111111',
          taskSummary: 'from session a',
          status: 'failed',
          spawnedAt: 1,
        },
        'sa-session-b-sub-22222222': {
          turnId: 'sa-session-b-sub-22222222',
          subSessionId: 'session-b-sub-22222222',
          taskSummary: 'from session b',
          status: 'running',
          spawnedAt: 2,
        },
      },
      'session-b',
    );

    expect(Object.keys(cards)).toEqual(['sa-session-b-sub-22222222']);
  });

  it('reads sub-agent card text from backend event fields', () => {
    expect(
      resolveSubAgentTaskSummary({
        task_summary: '回复一句 async sub agent ok',
        template: 'workspace-task-agent',
      }),
    ).toBe('回复一句 async sub agent ok');

    expect(
      resolveSubAgentTerminalOutput({
        success: false,
        error: '子代理执行失败',
      }),
    ).toBe('子代理执行失败');

    expect(
      resolveSubAgentTerminalOutput({
        success: true,
        reply: 'async sub agent ok',
      }),
    ).toBe('async sub agent ok');
  });

  it('uses backend event recordedAt as the sub-agent card time anchor', () => {
    expect(parseSessionEventTimestampMs('2026-06-13T17:07:30.000Z', 1)).toBe(
      Date.parse('2026-06-13T17:07:30.000Z'),
    );
    expect(parseSessionEventTimestampMs(1234, 1)).toBe(1234);
    expect(parseSessionEventTimestampMs('not-a-time', 5678)).toBe(5678);
  });

  it('routes an unmentioned chat message to the currently selected agent', () => {
    const agents = [
      agentDto({ agentId: 'assistant', name: '默认助手' }),
      agentDto({ agentId: 'consultant', name: '咨询专家' }),
    ];

    expect(
      resolveChatRoute('分析这段日志', agents, 'consultant'),
    ).toMatchObject({
      messageText: '分析这段日志',
      audience: 'agent',
      targetAgentIds: ['consultant'],
      primaryAgentId: 'consultant',
    });
  });

  it('routes @agent mentions by display name or agent id and strips the mention before sending', () => {
    const agents = [
      agentDto({ agentId: 'assistant', name: '默认助手' }),
      agentDto({
        agentId: 'consultant',
        name: '咨询专家',
        displayName: '顾问',
      }),
    ];

    expect(
      resolveChatRoute('@顾问 帮我评审方案', agents, 'assistant'),
    ).toMatchObject({
      messageText: '帮我评审方案',
      audience: 'agent',
      targetAgentIds: ['consultant'],
      primaryAgentId: 'consultant',
    });

    expect(
      resolveChatRoute('@assistant 继续', agents, 'consultant'),
    ).toMatchObject({
      messageText: '继续',
      targetAgentIds: ['assistant'],
    });
  });

  it('routes @all to every enabled and unfrozen workspace agent', () => {
    const agents = [
      agentDto({ agentId: 'assistant', name: '默认助手' }),
      agentDto({ agentId: 'consultant', name: '咨询专家' }),
      agentDto({ agentId: 'frozen', name: '冻结助手', isFrozen: true }),
    ];
    const route = resolveChatRoute('@all 给出各自建议', agents, 'assistant');

    expect(route).toMatchObject({
      messageText: '给出各自建议',
      audience: 'all',
      targetAgentIds: ['assistant', 'consultant'],
      primaryAgentId: 'assistant',
    });
    expect(getChatRouteLabel(route, agents)).toBe('all');
  });

  it('uses main session alias while preserving camera metadata', () => {
    const route = resolveChatRoute(
      '请分析这张图。',
      [agentDto({ agentId: 'assistant', name: '默认助手' })],
      'assistant',
    );

    expect(
      buildChatMessageRequest(route, 'session-1', 'assistant', false, {
        inputMode: 'camera',
        cameraSessionId: 'camera-session-1',
        visionArtifactId: 'vision-frame-1',
      }),
    ).toMatchObject({
      messageText: '请分析这张图。',
      sessionId: 'main',
      agentId: 'assistant',
      metadata: {
        inputMode: 'camera',
        cameraSessionId: 'camera-session-1',
        visionArtifactId: 'vision-frame-1',
      },
    });
  });

  it('projects realtime voice and camera session events into avatar runtime events', () => {
    expect(
      toChatInteractionRuntimeEvent(
        {
          type: 'voice_capture_status',
          status: 'Recording',
          voiceSessionId: 'voice-1',
        },
        'assistant',
      ),
    ).toMatchObject({
      type: 'voice_capture_status',
      agentId: 'assistant',
      status: 'recording',
      sessionId: 'voice-1',
    });

    expect(
      toChatInteractionRuntimeEvent(
        {
          type: 'camera_capture_status',
          status: 'capturing',
          sessionId: 'camera-1',
          artifactId: 'vision-frame-1',
        },
        'assistant',
      ),
    ).toMatchObject({
      type: 'camera_capture_status',
      agentId: 'assistant',
      status: 'capturing',
      sessionId: 'camera-1',
      artifactId: 'vision-frame-1',
    });

    expect(
      toChatInteractionRuntimeEvent({ type: 'delta', delta: 'x' }, 'assistant'),
    ).toBeNull();
  });

  it('removes only successfully injected steering messages from the active interaction queue', () => {
    const next = removeInjectedSteeringQueueItem(
      [
        {
          id: 'queued-1',
          text: '继续排队',
          createdAt: 1,
          status: 'queued',
        },
        {
          id: 'pending-1',
          text: '等待注入',
          createdAt: 2,
          status: 'steering_pending',
          steeringId: 'steering-1',
        },
        {
          id: 'injected-1',
          text: '已注入',
          createdAt: 3,
          status: 'steering_injected',
          steeringId: 'steering-1',
        },
        {
          id: 'failed-1',
          text: '失败保留',
          createdAt: 4,
          status: 'steering_failed',
          steeringId: 'steering-1',
        },
        {
          id: 'injected-2',
          text: '其他注入项',
          createdAt: 5,
          status: 'steering_injected',
          steeringId: 'steering-2',
        },
      ],
      'steering-1',
    );

    expect(next.map((item) => item.id)).toEqual([
      'queued-1',
      'pending-1',
      'failed-1',
      'injected-2',
    ]);
  });
});

describe('chat stream error diagnostics', () => {
  it('formats provider stream errors with log lookup fields', () => {
    const markdown = formatChatErrorDiagnostic(
      {
        type: 'error',
        message: 'LLM 调用失败: Too many requests',
        errorId: 'llm-abc',
        sessionId: 'session-1',
        messageId: '1667',
        traceId: 'trace-1',
        timestampUtc: '2026-06-28T08:27:47.700Z',
        location: 'agent.stream.llm_provider',
        errorCode: 'HTTP_429',
        round: 1,
        maxRounds: 200,
        modelId: 'deepseek-v4-flash',
        endpointHost: 'api.deepseek.com',
      },
      { turnId: 'local-turn' },
    );

    expect(markdown).toContain('## 请求失败');
    expect(markdown).toContain('Session ID: `session-1`');
    expect(markdown).toContain('Message ID / Turn ID: `1667`');
    expect(markdown).toContain('Trace ID: `trace-1`');
    expect(markdown).toContain('Error ID: `llm-abc`');
    expect(markdown).toContain('Location: `agent.stream.llm_provider`');
    expect(markdown).toContain('Error Code: `HTTP_429`');
    expect(markdown).toContain('Round: `1/200`');
    expect(markdown).toContain('Model: `deepseek-v4-flash`');
    expect(markdown).toContain('Endpoint Host: `api.deepseek.com`');
  });

  it('recognizes error done events and persisted diagnostic markdown', () => {
    expect(
      isChatStreamErrorEvent({
        type: 'done',
        reply: '## 请求失败',
        isError: true,
        errorId: 'llm-abc',
      }),
    ).toBe(true);
    expect(
      looksLikePersistedErrorDiagnostic(
        '## 请求失败\n\n### 诊断信息\n- Error ID: `llm-abc`\n- Location: `agent.stream.llm_provider`',
      ),
    ).toBe(true);
    expect(
      looksLikePersistedErrorDiagnostic(
        'Session fuse triggered. Session: session-1 State: Faulted Errors in window: 5 Action: stopped agent output, blocked further tool calls. Recovery: Send /resume to clear error counters and continue this session.',
      ),
    ).toBe(true);
  });
});

describe('compact result messages', () => {
  it('describes zero-compaction results as current session summary generation', () => {
    const text = formatCompactSuccessMessage({
      beforeTokens: 12,
      afterTokens: 12,
      compactedMessageCount: 0,
    });

    expect(text).toContain('已生成当前会话摘要');
    expect(text).not.toContain('覆盖 0 条');
  });

  it('does not claim summary generation when diagnostics show an empty summary', () => {
    const text = formatCompactSuccessMessage({
      sessionId: 'session-empty',
      summaryMessageId: '',
      mode: 'Manual',
      level: 'Full',
      beforeTokens: 12,
      afterTokens: 12,
      compactedMessageCount: 0,
      summaryPreview: '',
      diagnostics: {
        compactionId: 'compact-empty',
        previousSessionId: 'session-empty',
        previousLastMessageId: undefined,
        activeMessageCountBefore: 0,
        compactedMessageCount: 0,
        keptRecentMessageCount: 0,
        beforeTokens: 12,
        afterTokens: 12,
        summaryMessageId: '',
        summaryCharacterCount: 0,
        summaryEstimatedTokens: 0,
        completedAtUtc: '2026-06-28T09:10:11.0000000Z',
        durationMs: 10,
      },
    } as any);

    expect(text).toContain('当前没有可压缩的会话内容');
    expect(text).not.toContain('已生成当前会话摘要');
    expect(text).toContain('摘要大小：0 chars / 0 tokens');
  });

  it('keeps covered message count for real compaction results', () => {
    const text = formatCompactSuccessMessage({
      beforeTokens: 120,
      afterTokens: 40,
      compactedMessageCount: 8,
    });

    expect(text).toContain('覆盖 8 条历史消息');
    expect(text).toContain('Token 估算：120 → 40');
  });

  it('includes compaction diagnostics for log lookup', () => {
    const text = formatCompactSuccessMessage(
      {
        sessionId: 'session-before',
        summaryMessageId: 'summary-msg-1',
        mode: 'Manual',
        level: 'Full',
        beforeTokens: 120,
        afterTokens: 40,
        compactedMessageCount: 8,
        summaryPreview: 'summary',
        diagnostics: {
          compactionId: 'compact-uuid-1',
          previousSessionId: 'session-before',
          previousLastMessageId: 'msg-last',
          previousLastMessageSequence: 42,
          activeMessageCountBefore: 12,
          compactedMessageCount: 8,
          keptRecentMessageCount: 4,
          beforeTokens: 120,
          afterTokens: 40,
          summaryMessageId: 'summary-msg-1',
          summaryCharacterCount: 88,
          summaryEstimatedTokens: 22,
          summaryGenerator: 'CompositeContextCompactionSummaryGenerator',
          completedAtUtc: '2026-06-28T09:10:11.0000000Z',
          durationMs: 345,
          newSessionId: 'session-after',
          newSessionTitle: '压缩 - session-before',
        },
      } as any,
      { newSessionId: 'session-after', newSessionTitle: '压缩 - session-before' },
    );

    expect(text).toContain('压缩诊断');
    expect(text).toContain('Compaction ID：`compact-uuid-1`');
    expect(text).toContain('旧 Session：`session-before`');
    expect(text).toContain('最后消息：`msg-last`');
    expect(text).toContain('旧 Session 大小：120 tokens / 12 messages');
    expect(text).toContain('摘要大小：88 chars / 22 tokens');
    expect(text).toContain('摘要生成器：`CompositeContextCompactionSummaryGenerator`');
    expect(text).toContain('新 Session：`session-after`');
    expect(text).toContain('完成时间：`2026-06-28T09:10:11.0000000Z`');
  });
});
