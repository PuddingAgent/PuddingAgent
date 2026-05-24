import { render, screen } from '@testing-library/react';
import * as React from 'react';
import InputArea from './InputArea';

jest.mock('../styles', () => {
  const styles = new Proxy({}, {
    get: (_target, prop) => String(prop),
  });
  return {
    useChatStyles: () => ({
      styles,
    }),
  };
});

jest.mock('./CommandPalette', () => ({
  __esModule: true,
  COMMANDS: [],
  default: () => null,
}));

jest.mock('./ComposerActionMenu', () => () => null);
jest.mock('./ComposerFeedbackStrip', () => ({ state }: { state: { subAgentsRunning: number } }) => (
  <div data-testid="feedback-strip">子任务 {state.subAgentsRunning}</div>
));
jest.mock('./ComposerStatusDetails', () => ({ summary }: { summary: { subAgentsRunning: number } }) => (
  <div data-testid="status-details">运行中 {summary.subAgentsRunning}</div>
));
jest.mock('./VoiceInputButton', () => () => <button type="button">voice</button>);

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

describe('InputArea status feedback', () => {
  beforeEach(() => {
    jest.useFakeTimers();
  });

  afterEach(() => {
    jest.useRealTimers();
  });

  it('does not let the previous completed toast mask a new streaming state', () => {
    const { rerender } = render(
      <InputArea
        {...baseProps}
        status="completed"
      />,
    );

    expect(screen.getByText('· 已完成')).toBeTruthy();

    rerender(
      <InputArea
        {...baseProps}
        loading
        disabled
        status="streaming"
      />,
    );

    expect(screen.queryByText('· 已完成')).toBeNull();
    expect(screen.getByText('· 正在生成回复…')).toBeTruthy();
    expect(screen.getByPlaceholderText('正在生成回复…')).toBeTruthy();
  });

  it('shows the current session sub-agent count in the feedback strip', () => {
    render(React.createElement(InputArea as any, {
      ...baseProps,
      status: 'idle',
      subAgentsRunning: 2,
    }));

    expect(screen.getByText('子任务 2')).toBeTruthy();
  });

  it('keeps the send action mounted and enabled for multiline input', () => {
    render(React.createElement(InputArea as any, {
      ...baseProps,
      inputValue: '第一行\n第二行\n第三行\n第四行',
      status: 'idle',
    }));

    const sendButton = screen.getByTestId('chat-send') as HTMLButtonElement;

    expect(screen.getByTestId('composer-action-area')).toBeTruthy();
    expect(sendButton).toBeTruthy();
    expect(sendButton.disabled).toBe(false);
  });
});
