import type { ChatMessageBlock } from '../types';
import { projectExecutionFlow } from '../projections/executionFlowProjector';
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

  it('rerenders only the active row when parent delegation activity changes', () => {
    expect(
      areMessageRowPropsEqual(props, {
        ...props,
        parentDelegationActivity: {
          activeCount: 1,
          startedAt: 100,
          updatedAt: 200,
        },
      }),
    ).toBe(false);
  });

  it('rerenders every row when the focus view mode flips', () => {
    // P2#8：Focus view 切换是全局视图模式，任何一行都必须重新渲染
    expect(
      areMessageRowPropsEqual(props, { ...props, focusView: true }),
    ).toBe(false);
    expect(
      areMessageRowPropsEqual(props, { ...props, focusView: false }),
    ).toBe(true);
  });

  it('keeps a row stable when equivalent data is recreated under focus view', () => {
    expect(
      areMessageRowPropsEqual(
        { ...props, focusView: true },
        {
          ...props,
          focusView: true,
          block: {
            ...block,
            processItems: block.processItems?.map((item) => ({ ...item })),
          },
        },
      ),
    ).toBe(true);
  });

  it('rerenders only when this row receives a new execution-flow projection', () => {
    const projection = projectExecutionFlow([]);
    expect(
      areMessageRowPropsEqual(
        { ...props, executionFlowProjection: projection },
        { ...props, executionFlowProjection: projection },
      ),
    ).toBe(true);
    expect(
      areMessageRowPropsEqual(
        { ...props, executionFlowProjection: projection },
        { ...props, executionFlowProjection: projectExecutionFlow([]) },
      ),
    ).toBe(false);
  });
});
