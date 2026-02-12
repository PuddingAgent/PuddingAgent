export interface BrowserVoiceInputHandlers {
  onPermissionGranted?: (deviceLabel?: string) => void;
  onInterimTranscript?: (text: string) => void;
  onFinalTranscript?: (text: string) => void;
  onError?: (message: string) => void;
}

export interface BrowserVoiceInputHandle {
  stop: () => void;
}

export interface BrowserVoiceInputAdapter {
  isSupported: () => boolean;
  start: (
    handlers: BrowserVoiceInputHandlers,
  ) => Promise<BrowserVoiceInputHandle>;
}

function getSpeechRecognitionConstructor(): any {
  const host = globalThis as any;
  return host.SpeechRecognition || host.webkitSpeechRecognition;
}

function stopStream(stream: MediaStream): void {
  for (const track of stream.getTracks()) {
    track.stop();
  }
}

export const defaultBrowserVoiceInputAdapter: BrowserVoiceInputAdapter = {
  isSupported: () => {
    const mediaDevices = globalThis.navigator?.mediaDevices;
    return Boolean(
      typeof mediaDevices?.getUserMedia === 'function' &&
        getSpeechRecognitionConstructor(),
    );
  },

  async start(handlers) {
    const SpeechRecognition = getSpeechRecognitionConstructor();
    if (
      typeof globalThis.navigator?.mediaDevices?.getUserMedia !== 'function' ||
      !SpeechRecognition
    ) {
      throw new Error('当前浏览器不支持语音输入');
    }

    const stream = await globalThis.navigator.mediaDevices.getUserMedia({
      audio: true,
    });
    const recognition = new SpeechRecognition();
    let stopped = false;

    handlers.onPermissionGranted?.(stream.getAudioTracks()[0]?.label);

    recognition.lang = 'zh-CN';
    recognition.continuous = false;
    recognition.interimResults = true;

    recognition.onresult = (event: any) => {
      let interim = '';
      let final = '';
      for (
        let index = event.resultIndex;
        index < event.results.length;
        index += 1
      ) {
        const result = event.results[index];
        const text = String(result[0]?.transcript ?? '');
        if (result.isFinal) {
          final += text;
        } else {
          interim += text;
        }
      }
      if (interim.trim()) handlers.onInterimTranscript?.(interim.trim());
      if (final.trim()) handlers.onFinalTranscript?.(final.trim());
    };

    recognition.onerror = (event: any) => {
      handlers.onError?.(String(event.error || '语音识别失败'));
      stopStream(stream);
    };

    recognition.onend = () => {
      if (!stopped) stopStream(stream);
    };

    recognition.start();

    return {
      stop: () => {
        stopped = true;
        try {
          recognition.stop();
        } finally {
          stopStream(stream);
        }
      },
    };
  },
};
