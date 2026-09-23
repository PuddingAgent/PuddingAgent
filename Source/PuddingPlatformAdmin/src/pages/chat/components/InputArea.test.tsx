import {
  act,
  fireEvent,
  render,
  screen,
  waitFor,
} from '@testing-library/react';
import * as React from 'react';
import InputArea from './InputArea';

jest.mock('../styles', () => {
  const styles = new Proxy(
    {},
    {
      get: (_target, prop) => String(prop),
    },
  );
  return {
    useChatStyles: () => ({
      styles,
    }),
  };
});

jest.mock('./CommandPalette', () => ({
  __esModule: true,
  COMMANDS: [],
  filterCommands: () => [],
  default: () => null,
}));

jest.mock('./ComposerActionMenu', () => () => null);
// ContextUsageRing 是工具栏内的圆环控件（生产注释：用户诉求 2026-09-19，旧上下文指示条已移除、
// 改为圆环 + 点击出上下文明细面板）。**状态文案随 runtimeDetails 一起只在气泡打开时渲染**
// （生产：IntentConsole.tsx:969 把 <ComposerStatusDetails> 作为 runtimeDetails 传给圆环）
// ⇒ 默认 DOM 中永远看不到「· 已完成」。本用例考察的是「状态文案状态机」
//（completed 提示不得残留到 streaming），与圆环外壳无关 ⇒ 替身直接渲染 runtimeDetails。
jest.mock('./ContextUsageRing', () => {
  // 注意：jest.mock 工厂会被提升到 import 之前 ⇒ 不能直接引用外层 import 进来的 React，
  // 必须在工厂内部 require。
  const ReactLib = require('react');
  const Stub = ({ runtimeDetails }: { runtimeDetails?: unknown }) =>
    ReactLib.createElement('div', { 'data-testid': 'context-usage-ring' }, runtimeDetails);
  return { __esModule: true, default: Stub, ContextUsageRing: Stub };
});
jest.mock(
  './ComposerStatusDetails',
  () =>
    // 替身必须镜像生产契约：状态文案是通过 `summary.statusLabel` 渲染的
    //（生产：IntentConsole.tsx:736 `statusLabel: displayStatusText`；组件声明：ComposerStatusDetails.tsx:10）。
    // 本替身先前只渲染「运行中 N」而丢掉 statusLabel ⇒ 「· 已完成」永远不可能出现，
    // 使本文件的「completed toast」用例无端变红（属测试替身过期，不是生产缺陷）。
    ({ summary }: { summary: { subAgentsRunning: number; statusLabel: string } }) => (
      <div data-testid="status-details">
        <span>{summary.statusLabel}</span>
        <span>运行中 {summary.subAgentsRunning}</span>
      </div>
    ),
);
const baseProps = {
  inputValue: '',
  onInputChange: jest.fn(),
  onKeyDown: jest.fn(),
  loading: false,
  onSend: jest.fn(),
  onStop: jest.fn(),
  onExport: jest.fn(),
  disabled: false,
  tLimit: 0,
  tUsed: 0,
  tPct: 0,
};

const createVoiceAdapter = () => {
  let handlers: any;
  const handle = { stop: jest.fn() };
  return {
    adapter: {
      isSupported: () => true,
      start: jest.fn((nextHandlers: any) => {
        handlers = nextHandlers;
        handlers.onPermissionGranted?.('Built-in Microphone');
        return Promise.resolve(handle);
      }),
    },
    getHandlers: () => handlers,
    handle,
  };
};

describe('InputArea status feedback', () => {
  beforeEach(() => {
    jest.useFakeTimers();
  });

  afterEach(() => {
    jest.useRealTimers();
  });

  it('does not let the previous completed toast mask a new streaming state', () => {
    const { rerender } = render(
      <InputArea {...baseProps} status="completed" />,
    );

    expect(screen.getByText('· 已完成')).toBeTruthy();

    rerender(<InputArea {...baseProps} loading disabled status="streaming" />);

    expect(screen.queryByText('· 已完成')).toBeNull();
    expect(screen.getByText('· 正在生成回复…')).toBeTruthy();
    // 运行中的占位文案已被改成「排队/插嘴」引导（生产：IntentConsole.tsx:839，
    // loading ? '继续输入：Enter 排队，Ctrl/Cmd+Enter 插嘴当前 Agent…'
    //         : '输入你的问题或任务… 支持 @ 指派 Agent、/ 调用系统指令'）
    // ⇒ 旧期望「正在生成回复…」已不存在，按现状断言。
    expect(
      screen.getByPlaceholderText('继续输入：Enter 排队，Ctrl/Cmd+Enter 插嘴当前 Agent…'),
    ).toBeTruthy();
  });

  it('keeps IME composition drafts local until the final committed text', () => {
    const onInputChange = jest.fn();
    render(
      <InputArea {...baseProps} onInputChange={onInputChange} status="idle" />,
    );

    const input = screen.getByTestId('chat-input') as HTMLTextAreaElement;

    fireEvent.compositionStart(input);
    fireEvent.change(input, { target: { value: 'n' } });
    fireEvent.change(input, { target: { value: 'nihao' } });
    fireEvent.change(input, { target: { value: '你好' } });

    expect(onInputChange).not.toHaveBeenCalled();
    expect(input.value).toBe('你好');

    fireEvent.compositionEnd(input);

    expect(onInputChange).toHaveBeenCalledTimes(1);
    expect(onInputChange).toHaveBeenLastCalledWith('你好');
  });

  it('keeps the send action mounted and enabled for multiline input', () => {
    render(
      React.createElement(InputArea as any, {
        ...baseProps,
        inputValue: '第一行\n第二行\n第三行\n第四行',
        status: 'idle',
      }),
    );

    const sendButton = screen.getByTestId('chat-send') as HTMLButtonElement;

    expect(screen.getByTestId('composer-action-area')).toBeTruthy();
    expect(sendButton).toBeTruthy();
    expect(sendButton.disabled).toBe(false);
  });

  it('switches into voice mode and sends a transcript with voice metadata', async () => {
    const voice = createVoiceAdapter();
    const onSendWithMetadata = jest.fn();

    function ControlledInputArea() {
      const [value, setValue] = React.useState('请帮我');
      return React.createElement(InputArea as any, {
        ...baseProps,
        onInputChange: setValue,
        onSendWithMetadata,
        inputValue: value,
        status: 'idle',
        voiceInputAdapter: voice.adapter,
      });
    }

    render(<ControlledInputArea />);

    fireEvent.click(screen.getByRole('button', { name: '开始语音输入' }));
    expect(screen.getByTestId('voice-conversation-panel')).toBeTruthy();
    expect(screen.getByTestId('composer-action-area')).toBeTruthy();
    expect(screen.queryByText('语音会话')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: '开始语音会话' }));

    await waitFor(() => {
      expect(voice.adapter.start).toHaveBeenCalledTimes(1);
    });

    act(() => {
      voice.getHandlers().onInterimTranscript('整理今天');
    });
    expect(screen.getByDisplayValue('整理今天')).toBeTruthy();

    act(() => {
      voice.getHandlers().onFinalTranscript('整理今天的会议记录。');
    });

    await waitFor(() => {
      expect(screen.getByDisplayValue('整理今天的会议记录。')).toBeTruthy();
    });

    fireEvent.click(screen.getByRole('button', { name: '发送语音内容' }));

    await waitFor(() => {
      expect(onSendWithMetadata).toHaveBeenCalledWith(
        '整理今天的会议记录。',
        expect.objectContaining({
          inputMode: 'voice',
          asrProvider: 'browser',
          language: 'zh-CN',
        }),
      );
    });
    expect(voice.handle.stop).toHaveBeenCalledTimes(1);
  });

  it('shows the voice mode unavailable state when browser microphone capture is unavailable', () => {
    render(
      React.createElement(InputArea as any, {
        ...baseProps,
        status: 'idle',
        voiceInputAdapter: {
          isSupported: () => false,
          start: jest.fn(),
        },
      }),
    );

    fireEvent.click(screen.getByRole('button', { name: '开始语音输入' }));

    const button = screen.getByRole('button', { name: '开始语音会话' });

    expect((button as HTMLButtonElement).disabled).toBe(true);
    expect(
      screen.getByPlaceholderText('当前浏览器不支持语音输入。'),
    ).toBeTruthy();
  });
});
