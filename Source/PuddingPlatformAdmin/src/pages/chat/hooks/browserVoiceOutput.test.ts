import { defaultBrowserVoiceOutputAdapter } from './browserVoiceOutput';

describe('browserVoiceOutput', () => {
  const originalSpeechSynthesis = (globalThis as any).speechSynthesis;
  const originalSpeechSynthesisUtterance = (globalThis as any)
    .SpeechSynthesisUtterance;

  afterEach(() => {
    (globalThis as any).speechSynthesis = originalSpeechSynthesis;
    (globalThis as any).SpeechSynthesisUtterance =
      originalSpeechSynthesisUtterance;
  });

  it('speaks text through browser speech synthesis and exposes completion callbacks', () => {
    const speak = jest.fn();
    const cancel = jest.fn();
    let utterance: any;

    function SpeechSynthesisUtteranceMock(this: any, text: string) {
      utterance = this;
      this.text = text;
    }

    (globalThis as any).speechSynthesis = { speak, cancel };
    (globalThis as any).SpeechSynthesisUtterance = SpeechSynthesisUtteranceMock;

    const onStart = jest.fn();
    const onComplete = jest.fn();
    const onError = jest.fn();

    expect(defaultBrowserVoiceOutputAdapter.isSupported()).toBe(true);

    const handle = defaultBrowserVoiceOutputAdapter.speak(
      '整理今天的会议记录。',
      {
        lang: 'zh-CN',
        onStart,
        onComplete,
        onError,
      },
    );

    expect(utterance.text).toBe('整理今天的会议记录。');
    expect(utterance.lang).toBe('zh-CN');
    expect(speak).toHaveBeenCalledWith(utterance);

    utterance.onstart();
    utterance.onend();

    expect(onStart).toHaveBeenCalledTimes(1);
    expect(onComplete).toHaveBeenCalledTimes(1);
    expect(onError).not.toHaveBeenCalled();

    handle.stop();

    expect(cancel).toHaveBeenCalledTimes(1);
  });

  it('reports unsupported browsers without speaking', () => {
    (globalThis as any).speechSynthesis = undefined;
    (globalThis as any).SpeechSynthesisUtterance = undefined;

    expect(defaultBrowserVoiceOutputAdapter.isSupported()).toBe(false);
  });
});
