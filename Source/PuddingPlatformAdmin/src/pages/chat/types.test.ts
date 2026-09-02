import { buildMessageBlocks, type ChatTurn } from './types';

describe('buildMessageBlocks', () => {
  it('projects a managed Goal envelope as readable user-facing text', () => {
    const turn: ChatTurn = {
      turnId: 'turn-goal',
      userMessage: {
        id: 'goal-message',
        text: String.raw`internal prompt<goal_payload>{"objective":"\u5B8C\u6210\u8C03\u5EA6\u5668","iteration":2,"maxIterations":8}</goal_payload>`,
        timestamp: 1,
        status: 'success',
        metadata: {
          goal_managed: 'true',
          automation_origin: 'goal_continuation',
        },
      },
      assistant: {
        id: 'assistant-goal',
        status: 'thinking',
        timelineItems: [],
        answerMarkdown: '',
        isStreaming: false,
        renderMode: 'structured',
      },
    };

    const userBlock = buildMessageBlocks([turn]).find(
      (block) => block.role === 'user',
    );

    expect(userBlock?.content).toBe(
      'Goal 自动续行 · 第 2/8 轮\n\n完成调度器',
    );
  });

  it('creates an agent block for a thinking assistant before the first answer token', () => {
    const turns: ChatTurn[] = [
      {
        turnId: 'turn-thinking',
        userMessage: {
          id: 'user-1',
          text: '测试同步子代理',
          timestamp: 1,
          status: 'success',
        },
        assistant: {
          id: 'assistant-1',
          status: 'thinking',
          timelineItems: [],
          answerMarkdown: '',
          isStreaming: false,
          renderMode: 'structured',
        },
      },
    ];

    const blocks = buildMessageBlocks(turns, 'Pudding');

    expect(blocks).toHaveLength(2);
    expect(blocks[0]).toMatchObject({
      id: 'user-1:user',
      role: 'user',
      content: '测试同步子代理',
    });
    expect(blocks[1]).toMatchObject({
      id: 'assistant-1:assistant:0',
      role: 'agent',
      content: '',
      status: 'thinking',
      agentName: 'Pudding',
      isStreaming: false,
    });
  });

  it('filters sub-agent progress items but keeps spawned/completed delegation facts', () => {
    // 行为链升级：spawned/completed 是 DelegationRow 的父级有界事实，保留进
    // 主消息过程时间线；高频 subagent_progress 仍滤除（托盘坞承载）。
    const turns: ChatTurn[] = [
      {
        turnId: 'turn-sub-agent',
        userMessage: {
          id: 'user-1',
          text: '检查状态',
          timestamp: 1,
          status: 'success',
        },
        assistant: {
          id: 'assistant-1',
          status: 'success',
          timelineItems: [
            {
              id: 'sub-agent-1',
              type: 'subagent_spawned',
              status: 'running',
              name: 'sub-agent',
              message: '委派子任务',
              timestamp: 2,
              collapsed: false,
            },
            {
              id: 'sub-agent-1-progress',
              type: 'subagent_progress',
              status: 'running',
              name: 'sub-agent',
              text: '内部第 3 轮推理增量……',
              timestamp: 3,
              collapsed: true,
            },
            {
              id: 'thinking-1',
              type: 'thinking',
              text: 'main agent thinking',
              timestamp: 4,
              collapsed: true,
            },
          ],
          answerMarkdown: 'main answer',
          isStreaming: false,
          renderMode: 'structured',
        },
      },
    ];

    const blocks = buildMessageBlocks(turns, 'Pudding');
    const agentBlock = blocks.find((block) => block.role === 'agent');

    expect(agentBlock?.processItems).toEqual([
      expect.objectContaining({ id: 'sub-agent-1', type: 'subagent_spawned' }),
      expect.objectContaining({ id: 'thinking-1', type: 'thinking' }),
    ]);
    expect(
      agentBlock?.processItems?.some((item) => item.type === 'subagent_progress'),
    ).toBe(false);
  });

  it('carries planCard (P1#5) onto the agent message block', () => {
    const turns: ChatTurn[] = [
      {
        turnId: 'turn-plan',
        userMessage: {
          id: 'user-1',
          text: '规划一下这个功能',
          timestamp: 1,
          status: 'success',
        },
        assistant: {
          id: 'assistant-1',
          status: 'success',
          timelineItems: [],
          answerMarkdown: '计划如下',
          isStreaming: false,
          renderMode: 'structured',
          planCard: {
            planId: 'plan-1',
            summary: '为 Chat UI 实现 Plan 模式',
            steps: [
              { id: 's1', title: '调研 SSE 事件流' },
              { id: 's2', title: '实现投影', description: '前端' },
            ],
            status: 'pending',
            requestedAt: '2026-08-12T00:00:00.000Z',
          },
        },
      },
    ];

    const blocks = buildMessageBlocks(turns, 'Pudding');
    const agentBlock = blocks.find((block) => block.role === 'agent');

    expect(agentBlock?.planCard).toMatchObject({
      planId: 'plan-1',
      status: 'pending',
      steps: [
        { id: 's1', title: '调研 SSE 事件流' },
        { id: 's2', title: '实现投影', description: '前端' },
      ],
    });
  });
});
