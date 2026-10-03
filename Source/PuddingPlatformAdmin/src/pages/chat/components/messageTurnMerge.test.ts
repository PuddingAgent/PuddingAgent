// ── messageTurnMerge 定向单测：BUG2 双状态守卫 ──────────────────────────────
//  1. mergeActiveRunAssistant：本地 SSE 已终态时，轮询 activeRun 快照不得把
//     status/isStreaming 拉回运行态（状态条与正文两个状态来回翻转的根因之一）。
//  2. mergeProjectedMessageIntoTurns：同一 agent 消息（messageId 稳定）经投影
//     刷新重复到达时原地更新，不得追加为第二张卡片（轨迹卡/正文卡分裂）。
//  3. BUG3「生产了2个卡片」（2026-10-03 用户截图）：同一条回复同时以「历史
//     投影」与「activeRun 快照 / 本地直播 turn」两种身份存在时，必须归并成
//     一张卡片。守卫逐条锁死在下方 describe('BUG3 …') 中。
import type { AgentRunView, ConversationMessageView } from '../client/types';
import { buildMessageBlocks, extractVisionArtifactIds, type ChatTurn } from '../types';
import {
  hasProjectedUserTurn,
  mergeActiveRunAssistant,
  mergeActiveRunIntoTurns,
  mergeLocalTurnsAwaitingProjection,
  mergeProjectedMessageIntoTurns,
} from './MessageList';

const createAssistant = (
  overrides: Partial<ChatTurn['assistant']>,
): ChatTurn['assistant'] => ({
  id: 'msg-agent-1',
  status: 'streaming',
  timelineItems: [],
  answerMarkdown: '',
  isStreaming: true,
  renderMode: 'structured',
  ...overrides,
});

const createLocalTurn = (
  overrides: Partial<ChatTurn['assistant']>,
): ChatTurn => ({
  turnId: 'turn-1',
  source: {
    sourceId: 'agent',
    sourceType: 'agent',
    displayName: 'Pudding',
    avatarEmoji: '🤖',
    avatarColor: '#7c3aed',
  },
  userMessage: {
    id: 'client-1',
    text: '问题',
    timestamp: 1_000,
    status: 'success',
  },
  assistant: createAssistant(overrides),
});

const createAgentMessage = (
  overrides: Record<string, unknown> = {},
): ConversationMessageView =>
  ({
    messageId: 'msg-agent-1',
    role: 'agent',
    content: '回答正文',
    createdAt: '2026-08-23T00:00:01.000Z',
    status: 'streaming',
    sourceKind: 'agent',
    turnId: 'turn-1',
    runId: 'run-1',
    processItems: [],
    ...overrides,
  }) as unknown as ConversationMessageView;

const createUserMessage = (
  overrides: Partial<ConversationMessageView> = {},
): ConversationMessageView => ({
  messageId: 'msg-user-1',
  role: 'user',
  sourceId: 'user',
  sourceName: '我',
  content: '看一下这张图',
  createdAt: '2026-08-23T00:00:00.000Z',
  status: 'succeeded',
  processItems: [],
  ...overrides,
});

describe('canonical failed input without assistant reply', () => {
  it('keeps heartbeat prompt and exposes the failed outcome after reload', () => {
    const turns: ChatTurn[] = [];
    mergeProjectedMessageIntoTurns(turns, createUserMessage({
      role: 'system', sourceKind: 'system', sourceId: 'heartbeat', content: 'heartbeat prompt',
      turnOutcome: { status: 'failed', errorMessage: 'Visual inputs require a workspace and a vision-capable route.' },
    }), 'Pudding');
    const blocks = buildMessageBlocks(turns);
    expect(blocks).toHaveLength(1);
    expect(blocks[0]).toMatchObject({ role: 'heartbeat', status: 'error', content: 'heartbeat prompt', executionError: 'Visual inputs require a workspace and a vision-capable route.' });
  });

  it('shows a failed text turn even when there is no assistant message', () => {
    const turns: ChatTurn[] = [];
    mergeProjectedMessageIntoTurns(turns, createUserMessage({ turnOutcome: { status: 'failed', errorMessage: 'Provider failed' } }), 'Pudding');
    expect(buildMessageBlocks(turns)[1]).toMatchObject({ role: 'agent', status: 'error', content: 'Provider failed' });
  });
});

describe('mergeActiveRunAssistant 终态守卫', () => {
  it('本地已终态（success）时，滞后的 activeRun 快照不得回退 status/isStreaming', () => {
    const local = createAssistant({
      status: 'success',
      isStreaming: false,
      answerMarkdown: '完整回答',
    });
    const active = createAssistant({
      id: 'run-1:active-assistant',
      status: 'streaming',
      isStreaming: true,
      answerMarkdown: '完整回答',
    });

    const merged = mergeActiveRunAssistant(local, active);

    expect(merged.status).toBe('success');
    expect(merged.isStreaming).toBe(false);
    // 身份保持本地稳定
    expect(merged.id).toBe('msg-agent-1');
    expect(merged.answerMarkdown).toBe('完整回答');
  });

  it('本地运行中时仍接受 activeRun 快照合并（不误伤正常路径）', () => {
    const local = createAssistant({
      status: 'streaming',
      isStreaming: true,
      answerMarkdown: '部分',
    });
    const active = createAssistant({
      status: 'streaming',
      isStreaming: true,
      answerMarkdown: '部分回答更长',
    });

    const merged = mergeActiveRunAssistant(local, active);

    expect(merged.status).toBe('streaming');
    expect(merged.isStreaming).toBe(true);
    expect(merged.answerMarkdown).toBe('部分回答更长');
  });

  it('正文分叉（本地含直播竞态重复）→ 以服务端快照为准，不再「取更长」', () => {
    const local = createAssistant({
      status: 'streaming',
      isStreaming: true,
      // 直播竞态产生的重复版本（更长，但脏）
      answerMarkdown: '回答正文回答正文续',
    });
    const active = createAssistant({
      status: 'streaming',
      isStreaming: true,
      answerMarkdown: '回答正文续',
    });

    const merged = mergeActiveRunAssistant(local, active);

    expect(merged.answerMarkdown).toBe('回答正文续');
  });

  it('本地领先（服务端为前缀）→ 保留本地更长正文', () => {
    const local = createAssistant({
      status: 'streaming',
      isStreaming: true,
      answerMarkdown: '回答正文续（本地直播领先）',
    });
    const active = createAssistant({
      status: 'streaming',
      isStreaming: true,
      answerMarkdown: '回答正文续',
    });

    const merged = mergeActiveRunAssistant(local, active);

    expect(merged.answerMarkdown).toBe('回答正文续（本地直播领先）');
  });
});

describe('mergeProjectedMessageIntoTurns 同 messageId 原地更新', () => {
  it('同一 agent 消息刷新（answerMarkdown 已有）时更新原 turn，不追加第二张卡', () => {
    const turns: ChatTurn[] = [
      {
        ...createLocalTurn({}),
        assistant: createAssistant({
          id: 'msg-agent-1',
          answerMarkdown: '回答正',
          isStreaming: true,
        }),
      },
    ];

    mergeProjectedMessageIntoTurns(
      turns,
      createAgentMessage({ content: '回答正文' }),
      'Pudding',
    );

    expect(turns).toHaveLength(1);
    expect(turns[0].assistant.answerMarkdown).toBe('回答正文');
  });

  it('不同 messageId 且前一 turn 已有回答时，仍按新消息追加', () => {
    const turns: ChatTurn[] = [
      {
        ...createLocalTurn({}),
        assistant: createAssistant({
          id: 'msg-agent-1',
          answerMarkdown: '第一轮回答',
          isStreaming: false,
          status: 'success',
        }),
      },
    ];

    mergeProjectedMessageIntoTurns(
      turns,
      createAgentMessage({ messageId: 'msg-agent-2', content: '第二轮回答' }),
      'Pudding',
    );

    expect(turns).toHaveLength(2);
    expect(turns[1].assistant.answerMarkdown).toBe('第二轮回答');
  });
});

// ── BUG3「生产了2个卡片」：同一条回复的两种表示必须归并 ─────────────────────
const REPLY = '现在是 2026-10-03（星期六）07:50:40，UTC+8。\n\n顺带一提：免打扰时段。';

/** 历史投影：用户消息行（turn_id 为空）+ 助手消息行（turn_id 有值）→ 两条 turn。 */
const createProjectedHistoryTurns = (
  commandClientId = 'msg-1790985034957-djqyq5w2',
  turnId = 'turn-e094f08e',
): ChatTurn[] => {
  const turns: ChatTurn[] = [];
  mergeProjectedMessageIntoTurns(
    turns,
    createUserMessage({
      messageId: commandClientId,
      content: '你好几点了',
      turnId: null,
      runId: null,
      createdAt: '2026-10-02T23:50:35.034Z',
      status: 'succeeded',
    }),
    'Pudding',
  );
  mergeProjectedMessageIntoTurns(
    turns,
    createAgentMessage({
      messageId: 'msg-7d93915207e3',
      turnId,
      runId: 'run-653d86c0',
      content: REPLY,
      status: 'succeeded',
      createdAt: '2026-10-02T23:50:47.000Z',
    }),
    'Pudding',
  );
  return turns;
};

const createActiveRun = (
  overrides: Partial<AgentRunView> = {},
): AgentRunView => ({
  runId: 'run-653d86c0',
  workspaceId: 'default',
  ownerUserId: 'user-1',
  agentId: 'agent-1',
  mainSessionId: 'session-1',
  commandClientId: 'msg-1790985034957-djqyq5w2',
  status: 'running',
  statusText: 'running',
  summary: '',
  eventCursor: 10,
  outputSnapshot: { markdown: REPLY, processItems: [] },
  startedAt: '2026-10-02T23:50:36.905Z',
  updatedAt: '2026-10-02T23:50:47.000Z',
  ...overrides,
});

describe('BUG3 同一条回复不得渲染成两张卡片', () => {
  it('本地 turn 没有用户正文时，turnId 身份仍可与投影匹配（不再被当作新 turn）', () => {
    const projected = createProjectedHistoryTurns();
    const liveTurn: ChatTurn = {
      turnId: 'turn-e094f08e',
      userMessage: {
        id: 'umsg:recovered:msg-7d93915207e3',
        text: '',
        timestamp: 1_790_985_047_000,
        status: 'success',
      },
      assistant: createAssistant({ answerMarkdown: '', status: 'streaming' }),
    };

    expect(hasProjectedUserTurn(projected, liveTurn)).toBe(true);
  });

  it('直播影子 turn（无用户正文 + 同 turnId）不追加第二张卡', () => {
    const projected = createProjectedHistoryTurns();
    const liveTurn: ChatTurn = {
      turnId: 'turn-e094f08e',
      userMessage: {
        id: 'umsg:recovered:msg-7d93915207e3',
        text: '',
        timestamp: 1_790_985_047_000,
        status: 'success',
      },
      assistant: createAssistant({ answerMarkdown: '', status: 'streaming' }),
    };

    const merged = mergeLocalTurnsAwaitingProjection(projected, [liveTurn]);
    const agentBlocks = buildMessageBlocks(merged).filter(
      (block) => block.role === 'agent',
    );
    expect(agentBlocks).toHaveLength(1);
  });

  it('身份锚点全失配但正文相同的直播影子，仍不追加第二张卡', () => {
    const projected = createProjectedHistoryTurns();
    const shadowTurn: ChatTurn = {
      turnId: 'run-653d86c0',
      userMessage: {
        id: 'run-653d86c0:active-user-placeholder',
        text: '',
        timestamp: 1_790_985_036_905,
        status: 'success',
      },
      assistant: createAssistant({
        answerMarkdown: REPLY,
        status: 'streaming',
      }),
    };

    const merged = mergeLocalTurnsAwaitingProjection(projected, [shadowTurn]);
    expect(
      buildMessageBlocks(merged).filter((block) => block.role === 'agent'),
    ).toHaveLength(1);
  });

  it('activeRun 正文只差空白时归并到历史 turn，不追加第二张卡', () => {
    const turns = createProjectedHistoryTurns();

    const merged = mergeActiveRunIntoTurns(
      turns,
      createActiveRun(),
      'Pudding',
      // 直播快照常见的形态差异：尾部换行 + 段落间多一个空行
      REPLY.replace('\n\n', '\n\n\n') + '\n',
    );

    expect(merged).toHaveLength(turns.length);
    const agentBlocks = buildMessageBlocks(merged).filter(
      (block) => block.role === 'agent',
    );
    expect(agentBlocks).toHaveLength(1);
    expect(agentBlocks[0].content).toContain('2026-10-03');
  });

  it('正文比较失配时按 commandClientId 身份归并（用户行/助手行被拆成两条 turn）', () => {
    const turns = createProjectedHistoryTurns();

    const merged = mergeActiveRunIntoTurns(
      turns,
      createActiveRun({
        // 快照正文与持久化正文语义不同（例如只剩过程摘要）
        outputSnapshot: { markdown: '', processItems: [] },
      }),
      'Pudding',
      '',
    );

    expect(merged).toHaveLength(turns.length);
    expect(
      buildMessageBlocks(merged).filter((block) => block.role === 'agent'),
    ).toHaveLength(1);
  });

  it('真正的第二轮 activeRun（没有已物化回复）仍然追加为新卡片', () => {
    const turns = createProjectedHistoryTurns();

    const merged = mergeActiveRunIntoTurns(
      turns,
      createActiveRun({
        runId: 'run-next',
        commandClientId: 'msg-next',
        outputSnapshot: { markdown: '第二轮回答', processItems: [] },
        startedAt: '2026-10-02T23:51:00.000Z',
      }),
      'Pudding',
      '第二轮回答',
    );

    expect(merged).toHaveLength(turns.length + 1);
    expect(merged[merged.length - 1].assistant.answerMarkdown).toBe(
      '第二轮回答',
    );
  });
});

// ── canonical 投影用户图片部件透传（V6-T8）────────────────────────────────
//  conversationView.messages 的 canonical 投影曾丢失 contentParts，导致用户
//  图片经投影后降级为「图片」占位符。此锁定：用户消息必须透传 contentParts。
describe('createProjectedTurn 用户消息 contentParts 透传', () => {
  it('投影用户消息时保留 canonical contentParts，图片 artifactId 可被消费', () => {
    const turns: ChatTurn[] = [];

    mergeProjectedMessageIntoTurns(
      turns,
      createUserMessage({
        contentParts: [
          { type: 'image', artifactId: 'vision-a', detail: 'original' },
        ],
      }),
      'Pudding',
    );

    expect(turns).toHaveLength(1);
    expect(turns[0].userMessage.contentParts).toEqual([
      { type: 'image', artifactId: 'vision-a', detail: 'original' },
    ]);
    expect(extractVisionArtifactIds(turns[0])).toEqual(['vision-a']);
  });

  it('非用户消息不携带 contentParts（不污染 agent 投影）', () => {
    const turns: ChatTurn[] = [];

    mergeProjectedMessageIntoTurns(
      turns,
      createAgentMessage({
        contentParts: [{ type: 'image', artifactId: 'vision-a' }],
      }),
      'Pudding',
    );

    expect(turns).toHaveLength(1);
    expect(turns[0].userMessage.contentParts).toBeUndefined();
  });
});
