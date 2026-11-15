import { recognizeAsr } from '@/services/platform/api';
import { createVoiceInputAdapter } from './cloudVoiceInput';

jest.mock('@/services/platform/api', () => ({ recognizeAsr: jest.fn() }));

describe('cloud voice capture completion and cancellation', () => {
  let port: any;
  let track: any;
  const previousContext = globalThis.AudioContext;
  const previousNode = globalThis.AudioWorkletNode;
  const previousMedia = navigator.mediaDevices;
  beforeEach(() => {
    track = { stop: jest.fn(), onended: null, label: 'microphone' };
    Object.defineProperty(navigator, 'mediaDevices', { configurable: true, value: {
      getUserMedia: jest.fn().mockResolvedValue({ getTracks: () => [track], getAudioTracks: () => [track] }),
    } });
    (globalThis as any).AudioContext = class {
      sampleRate = 16000; state = 'running'; destination = {};
      resume = jest.fn().mockResolvedValue(undefined); close = jest.fn().mockResolvedValue(undefined);
      audioWorklet = { addModule: jest.fn().mockResolvedValue(undefined) };
      createMediaStreamSource = () => ({ connect: jest.fn(), disconnect: jest.fn() });
    };
    (globalThis as any).AudioWorkletNode = class {
      port = { onmessage: null as any, postMessage: () => queueMicrotask(() => this.port.onmessage({ data: 'finished' })) };
      connect = jest.fn(); disconnect = jest.fn();
      constructor() { port = this.port; }
    };
    (recognizeAsr as jest.Mock).mockReset();
  });
  afterEach(() => {
    (globalThis as any).AudioContext = previousContext; (globalThis as any).AudioWorkletNode = previousNode;
    Object.defineProperty(navigator, 'mediaDevices', { configurable: true, value: previousMedia });
  });
  it('cancel releases the microphone and never submits recognition', async () => {
    const final = jest.fn();
    const handle = await createVoiceInputAdapter().start({ onFinalTranscript: final });
    port.onmessage({ data: new Float32Array(200) });
    handle.cancel(); handle.stop(); await Promise.resolve();
    expect(track.stop).toHaveBeenCalled(); expect(recognizeAsr).not.toHaveBeenCalled(); expect(final).not.toHaveBeenCalled();
  });
  it('waits for the actual HTTP completion and ignores a late result after cancellation', async () => {
    let resolve!: (value: any) => void;
    (recognizeAsr as jest.Mock).mockReturnValue(new Promise((done) => { resolve = done; }));
    const final = jest.fn();
    const handle = await createVoiceInputAdapter().start({ onFinalTranscript: final });
    port.onmessage({ data: new Float32Array(200) }); handle.stop();
    await Promise.resolve(); await Promise.resolve();
    expect(recognizeAsr).toHaveBeenCalled(); expect(final).not.toHaveBeenCalled();
    const signal = (recognizeAsr as jest.Mock).mock.calls[0][1] as AbortSignal;
    handle.cancel(); resolve({ text: 'late transcript' }); await Promise.resolve(); await Promise.resolve();
    expect(signal.aborted).toBe(true); expect(final).not.toHaveBeenCalled();
  });
});
