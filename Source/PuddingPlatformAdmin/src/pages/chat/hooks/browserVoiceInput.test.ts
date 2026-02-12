import { defaultBrowserVoiceInputAdapter } from './browserVoiceInput';

describe('browserVoiceInput', () => {
  const originalNavigator = globalThis.navigator;
  const originalSpeechRecognition = (globalThis as any).SpeechRecognition;
  const originalWebkitSpeechRecognition = (globalThis as any)
    .webkitSpeechRecognition;

  afterEach(() => {
    Object.defineProperty(globalThis, 'navigator', {
      configurable: true,
      value: originalNavigator,
    });
    (globalThis as any).SpeechRecognition = originalSpeechRecognition;
    (globalThis as any).webkitSpeechRecognition =
      originalWebkitSpeechRecognition;
  });

  it('requests microphone permission, streams speech recognition text, and stops tracks', async () => {
    const stopTrack = jest.fn();
    const getUserMedia = jest.fn().mockResolvedValue({
      getTracks: () => [{ stop: stopTrack }],
      getAudioTracks: () => [{ label: 'Built-in Mic', stop: stopTrack }],
    });
    const recognitionStart = jest.fn();
    const recognitionStop = jest.fn();
    let recognition: any;

    function SpeechRecognitionMock(this: any) {
      recognition = this;
      this.start = recognitionStart;
      this.stop = recognitionStop;
    }

    Object.defineProperty(globalThis, 'navigator', {
      configurable: true,
      value: {
        mediaDevices: { getUserMedia },
      },
    });
    (globalThis as any).SpeechRecognition = SpeechRecognitionMock;

    const handlers = {
      onPermissionGranted: jest.fn(),
      onInterimTranscript: jest.fn(),
      onFinalTranscript: jest.fn(),
      onError: jest.fn(),
    };

    expect(defaultBrowserVoiceInputAdapter.isSupported()).toBe(true);

    const handle = await defaultBrowserVoiceInputAdapter.start(handlers);

    expect(getUserMedia).toHaveBeenCalledWith({ audio: true });
    expect(handlers.onPermissionGranted).toHaveBeenCalledWith('Built-in Mic');
    expect(recognition.interimResults).toBe(true);
    expect(recognition.lang).toBe('zh-CN');
    expect(recognitionStart).toHaveBeenCalledTimes(1);

    recognition.onresult({
      resultIndex: 0,
      results: [
        { 0: { transcript: '整理' }, isFinal: false },
        { 0: { transcript: '今天的记录。' }, isFinal: true },
      ],
    });

    expect(handlers.onInterimTranscript).toHaveBeenCalledWith('整理');
    expect(handlers.onFinalTranscript).toHaveBeenCalledWith('今天的记录。');

    handle.stop();

    expect(recognitionStop).toHaveBeenCalledTimes(1);
    expect(stopTrack).toHaveBeenCalled();
  });

  it('reports unsupported browsers without starting capture', () => {
    Object.defineProperty(globalThis, 'navigator', {
      configurable: true,
      value: { mediaDevices: undefined },
    });
    (globalThis as any).SpeechRecognition = undefined;
    (globalThis as any).webkitSpeechRecognition = undefined;

    expect(defaultBrowserVoiceInputAdapter.isSupported()).toBe(false);
  });
});
