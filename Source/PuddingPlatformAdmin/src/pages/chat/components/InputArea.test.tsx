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
jest.mock(
  './ComposerFeedbackStrip',
  () =>
    ({ state }: { state: { subAgentsRunning: number } }) => (
      <div data-testid="feedback-strip">子任务 {state.subAgentsRunning}</div>
    ),
);
jest.mock(
  './ComposerStatusDetails',
  () =>
    ({ summary }: { summary: { subAgentsRunning: number } }) => (
      <div data-testid="status-details">运行中 {summary.subAgentsRunning}</div>
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
    expect(screen.getByPlaceholderText('正在生成回复…')).toBeTruthy();
  });

  it('dismisses the completed status when the user types and clears text', () => {
    function ControlledInputArea() {
      const [value, setValue] = React.useState('');
      return (
        <InputArea
          {...baseProps}
          inputValue={value}
          onInputChange={setValue}
          status="completed"
        />
      );
    }

    render(<ControlledInputArea />);

    expect(screen.getByText('· 已完成')).toBeTruthy();

    const input = screen.getByTestId('chat-input') as HTMLTextAreaElement;
    fireEvent.change(input, { target: { value: 'hello' } });
    fireEvent.change(input, { target: { value: '' } });

    expect(screen.queryByText('· 已完成')).toBeNull();
    expect(screen.getByPlaceholderText('输入你的问题或任务…')).toBeTruthy();
  });

  it('keeps the completed status hidden while the focused input is empty', () => {
    function ControlledInputArea() {
      const [value, setValue] = React.useState('');
      return (
        <InputArea
          {...baseProps}
          inputValue={value}
          onInputChange={setValue}
          status="completed"
        />
      );
    }

    render(<ControlledInputArea />);

    expect(screen.getByText('· 已完成')).toBeTruthy();

    const input = screen.getByTestId('chat-input') as HTMLTextAreaElement;
    fireEvent.focus(input);
    fireEvent.change(input, { target: { value: 'hello' } });
    fireEvent.change(input, { target: { value: '' } });

    expect(screen.queryByText('· 已完成')).toBeNull();
    expect(
      (screen.getByTestId('chat-send') as HTMLButtonElement).disabled,
    ).toBe(true);
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

  it('shows the current session sub-agent count in the feedback strip', () => {
    render(
      React.createElement(InputArea as any, {
        ...baseProps,
        status: 'idle',
        subAgentsRunning: 2,
      }),
    );

    expect(screen.getByText('子任务 2')).toBeTruthy();
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
