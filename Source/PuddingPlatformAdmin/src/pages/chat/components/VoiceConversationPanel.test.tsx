import {
  act,
  fireEvent,
  render,
  screen,
  waitFor,
} from '@testing-library/react';
import * as React from 'react';
import VoiceConversationPanel from './VoiceConversationPanel';

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

const createVoiceInputAdapter = () => {
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

function ControlledVoicePanel(props: any = {}) {
  const [value, setValue] = React.useState(props.inputValue ?? '');
  return (
    <VoiceConversationPanel
      inputValue={value}
      disabled={false}
      loading={false}
      latestAssistantText="这是最新回复。"
      voiceInputAdapter={createVoiceInputAdapter().adapter}
      onDraftChange={setValue}
      onSend={jest.fn()}
      {...props}
    />
  );
}

describe('VoiceConversationPanel', () => {
  it('captures speech into an editable confirmation draft and sends voice metadata', async () => {
    const voice = createVoiceInputAdapter();
    const onSend = jest.fn();
    const onVoiceCaptureStatus = jest.fn();

    render(
      <ControlledVoicePanel
        voiceInputAdapter={voice.adapter}
        onSend={onSend}
        onVoiceCaptureStatus={onVoiceCaptureStatus}
      />,
    );

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

    fireEvent.change(screen.getByLabelText('语音转写草稿'), {
      target: { value: '整理今天的会议记录，并列出行动项。' },
    });
    fireEvent.click(screen.getByRole('button', { name: '发送语音内容' }));

    await waitFor(() => {
      expect(onSend).toHaveBeenCalledWith(
        '整理今天的会议记录，并列出行动项。',
        expect.objectContaining({
          inputMode: 'voice',
          asrProvider: 'browser',
          language: 'zh-CN',
        }),
      );
    });
    expect(onVoiceCaptureStatus).toHaveBeenCalledWith(
      'recording',
      expect.any(Object),
    );
    expect(onVoiceCaptureStatus).toHaveBeenCalledWith(
      'completed',
      expect.any(Object),
    );
    expect(voice.handle.stop).toHaveBeenCalledTimes(1);
  });

  it('speaks the latest assistant answer and can stop playback', async () => {
    const handle = { stop: jest.fn() };
    let handlers: any;
    const voiceOutputAdapter = {
      isSupported: () => true,
      speak: jest.fn((_text: string, nextHandlers: any) => {
        handlers = nextHandlers;
        handlers.onStart?.();
        return handle;
      }),
    };
    const onVoicePlaybackStatus = jest.fn();

    render(
      <ControlledVoicePanel
        latestAssistantText="请按这三步处理。"
        voiceOutputAdapter={voiceOutputAdapter}
        onVoicePlaybackStatus={onVoicePlaybackStatus}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: '朗读最新回复' }));

    expect(voiceOutputAdapter.speak).toHaveBeenCalledWith(
      '请按这三步处理。',
      expect.objectContaining({ lang: 'zh-CN' }),
    );
    expect(onVoicePlaybackStatus).toHaveBeenCalledWith(
      'synthesizing',
      expect.any(Object),
    );
    expect(onVoicePlaybackStatus).toHaveBeenCalledWith(
      'playing',
      expect.any(Object),
    );

    fireEvent.click(await screen.findByRole('button', { name: '停止朗读' }));

    expect(handle.stop).toHaveBeenCalledTimes(1);
    expect(onVoicePlaybackStatus).toHaveBeenCalledWith(
      'cancelled',
      expect.any(Object),
    );

    act(() => {
      handlers.onComplete?.();
    });
  });

  it('announces voice state changes without relying on color only', async () => {
    render(<ControlledVoicePanel />);

    fireEvent.click(screen.getByRole('button', { name: '开始语音会话' }));

    await waitFor(() => {
      expect(screen.getByRole('status').textContent).toMatch(
        /正在听|正在转写|待确认/,
      );
    });
  });
});
