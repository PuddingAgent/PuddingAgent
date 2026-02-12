export interface BrowserVoiceOutputHandlers {
  lang?: string;
  onStart?: () => void;
  onComplete?: () => void;
  onError?: (message: string) => void;
}

export interface BrowserVoiceOutputHandle {
  stop: () => void;
}

export interface BrowserVoiceOutputAdapter {
  isSupported: () => boolean;
  speak: (
    text: string,
    handlers?: BrowserVoiceOutputHandlers,
  ) => BrowserVoiceOutputHandle;
}

export const defaultBrowserVoiceOutputAdapter: BrowserVoiceOutputAdapter = {
  isSupported: () =>
    Boolean(
      (globalThis as any).speechSynthesis &&
        (globalThis as any).SpeechSynthesisUtterance,
    ),

  speak(text, handlers = {}) {
    const speechSynthesis = (globalThis as any).speechSynthesis;
    const SpeechSynthesisUtterance = (globalThis as any)
      .SpeechSynthesisUtterance;
    if (!speechSynthesis || !SpeechSynthesisUtterance) {
      throw new Error('当前浏览器不支持语音朗读');
    }

    const utterance = new SpeechSynthesisUtterance(text);
    utterance.lang = handlers.lang || 'zh-CN';
    utterance.onstart = () => handlers.onStart?.();
    utterance.onend = () => handlers.onComplete?.();
    utterance.onerror = (event: any) => {
      handlers.onError?.(String(event.error || '语音朗读失败'));
    };

    speechSynthesis.speak(utterance);

    return {
      stop: () => {
        speechSynthesis.cancel();
      },
    };
  },
};
