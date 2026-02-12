import { buildMessageBlocks, type ChatTurn } from './types';

describe('buildMessageBlocks', () => {
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
      role: 'user',
      content: '测试同步子代理',
    });
    expect(blocks[1]).toMatchObject({
      role: 'agent',
      content: '',
      status: 'thinking',
      agentName: 'Pudding',
      isStreaming: false,
    });
  });
});
