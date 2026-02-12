import { fireEvent, render, screen } from '@testing-library/react';
import * as React from 'react';
import VoiceInputButton from './VoiceInputButton';

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

describe('VoiceInputButton', () => {
  it('does not simulate recording before the voice pipeline is connected', () => {
    const onVoiceStart = jest.fn();
    const onVoiceStop = jest.fn();

    render(
      <VoiceInputButton
        onVoiceStart={onVoiceStart}
        onVoiceStop={onVoiceStop}
      />,
    );

    const button = screen.getByRole('button', { name: '语音输入待接入' });

    expect((button as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(button);
    expect(onVoiceStart).not.toHaveBeenCalled();
    expect(onVoiceStop).not.toHaveBeenCalled();
    expect(screen.queryByLabelText('语音录制波形')).toBeNull();
  });

  it('starts voice input when the browser voice pipeline is available', () => {
    const onVoiceStart = jest.fn();

    render(
      <VoiceInputButton enabled status="idle" onVoiceStart={onVoiceStart} />,
    );

    const button = screen.getByRole('button', { name: '开始语音输入' });
    expect((button as HTMLButtonElement).disabled).toBe(false);

    fireEvent.click(button);

    expect(onVoiceStart).toHaveBeenCalledTimes(1);
  });

  it('stops active voice input and shows a quiet transcript preview', () => {
    const onVoiceStart = jest.fn();
    const onVoiceStop = jest.fn();

    render(
      <VoiceInputButton
        enabled
        status="recording"
        transcriptPreview="整理今天的"
        onVoiceStart={onVoiceStart}
        onVoiceStop={onVoiceStop}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: '停止语音输入' }));

    expect(onVoiceStart).not.toHaveBeenCalled();
    expect(onVoiceStop).toHaveBeenCalledTimes(1);
    expect(screen.getByText('整理今天的')).toBeTruthy();
    expect(screen.queryByLabelText('语音录制波形')).toBeNull();
  });
});
