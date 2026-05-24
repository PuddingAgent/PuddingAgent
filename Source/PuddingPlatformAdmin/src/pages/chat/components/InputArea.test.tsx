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
jest.mock('./ComposerFeedbackStrip', () => () => <div data-testid="feedback-strip" />);
jest.mock('./ComposerStatusDetails', () => () => <div data-testid="status-details" />);
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
});
