import type { ChatMessageBlock } from '../types';
import { areMessageRowPropsEqual } from './MessageRow';

const formatTime = (timestamp: number) => String(timestamp);
const onContextMenu = jest.fn();
const onRerunTurn = jest.fn();
const onPinTurn = jest.fn();
const onDeleteTurn = jest.fn();

const block: ChatMessageBlock = {
  id: 'assistant-1',
  turnId: 'turn-1',
  role: 'agent',
  content: '历史回复',
  status: 'success',
  createdAt: 1,
  agentId: 'agent-1',
  agentName: 'Pudding',
  processItems: [
    {
      id: 'tool-1',
      type: 'tool_result',
      status: 'success',
      output: 'done',
      timestamp: 1,
      collapsed: true,
    },
  ],
};

const props = {
  block,
  sessionId: 'session-1',
  workspaceId: 'default',
  defaultAvatarUrl: '/avatar.png',
  formatTime,
  onContextMenu,
  onRerunTurn,
  onPinTurn,
  onDeleteTurn,
};

describe('MessageRow memo boundary', () => {
  it('keeps a historical row stable when projection recreates equivalent data', () => {
    expect(
      areMessageRowPropsEqual(props, {
        ...props,
        block: {
          ...block,
          processItems: block.processItems?.map((item) => ({ ...item })),
        },
      }),
    ).toBe(true);
  });

  it('rerenders when visible content changes', () => {
    expect(
      areMessageRowPropsEqual(props, {
        ...props,
        block: { ...block, content: '流式回复新增内容' },
      }),
    ).toBe(false);
  });

  it('rerenders when process output changes before answer text arrives', () => {
    expect(
      areMessageRowPropsEqual(props, {
        ...props,
        block: {
          ...block,
          processItems: block.processItems?.map((item) => ({
            ...item,
            output: 'updated',
          })),
        },
      }),
    ).toBe(false);
  });
});
