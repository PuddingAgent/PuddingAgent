import { connectRealtimeAsr } from '@/services/platform/realtimeAsr';
import { createVoiceInputAdapter } from './cloudVoiceInput';
import { waitFor } from '@testing-library/react';

jest.mock('@/services/platform/realtimeAsr', () => ({ connectRealtimeAsr: jest.fn() }));

describe('live capture, completion and cancellation', () => {
  let port: any; let track: any; let callbacks: any; let connection: any;
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
      connect = jest.fn(); disconnect = jest.fn(); constructor() { port = this.port; }
    };
    connection = { sendAudio: jest.fn(), finish: jest.fn(), cancel: jest.fn() };
    (connectRealtimeAsr as jest.Mock).mockImplementation(async (handlers: any) => { callbacks = handlers; return connection; });
  });
  afterEach(() => {
    (globalThis as any).AudioContext = previousContext; (globalThis as any).AudioWorkletNode = previousNode;
    Object.defineProperty(navigator, 'mediaDevices', { configurable: true, value: previousMedia });
  });
  it('uploads PCM while recording and delivers intermediate text before stop', async () => {
    const transcript = jest.fn(); const final = jest.fn();
    const handle = await createVoiceInputAdapter().start({ onTranscript: transcript, onFinalTranscript: final });
    port.onmessage({ data: new Float32Array([0.5, -0.5]) });
    await waitFor(() => expect(connection.sendAudio).toHaveBeenCalledTimes(1)); expect(connection.finish).not.toHaveBeenCalled();
    expect(Array.from(new Int16Array(connection.sendAudio.mock.calls[0][0]))).toEqual([16383, -16384]);
    callbacks.onTranscript('临时', ''); expect(transcript).toHaveBeenCalledWith('临时', '');
    handle.stop(); await waitFor(() => expect(connection.finish).toHaveBeenCalledTimes(1));
    expect(track.stop).toHaveBeenCalled(); expect(connection.finish).toHaveBeenCalledTimes(1);
    expect(final).not.toHaveBeenCalled(); callbacks.onCompleted('确认。'); expect(final).toHaveBeenCalledWith('确认。');
  });
  it('cancel during drain closes the connection and ignores late text', async () => {
    const final = jest.fn(); const transcript = jest.fn();
    const handle = await createVoiceInputAdapter().start({ onFinalTranscript: final, onTranscript: transcript });
    handle.stop(); await Promise.resolve(); handle.cancel(); callbacks.onCompleted('迟到'); callbacks.onTranscript('迟到', '');
    expect(connection.cancel).toHaveBeenCalled(); expect(track.stop).toHaveBeenCalled();
    expect(final).not.toHaveBeenCalled(); expect(transcript).not.toHaveBeenCalled();
  });
  it('cancels a microphone permission request even when the device arrives later', async () => {
    let grant!: (stream: unknown) => void;
    (navigator.mediaDevices.getUserMedia as jest.Mock).mockReturnValue(new Promise(resolve => { grant = resolve; }));
    const controller = new AbortController(); const pending = createVoiceInputAdapter().start({}, controller.signal);
    controller.abort(); grant({ getTracks: () => [track] }); await expect(pending).rejects.toThrow('录音已取消');
    expect(track.stop).toHaveBeenCalled(); expect(connection.sendAudio).not.toHaveBeenCalled();
  });
  it('microphone disconnect releases the session and reports an actionable error', async () => {
    const error = jest.fn(); await createVoiceInputAdapter().start({ onError: error }); track.onended();
    expect(connection.cancel).toHaveBeenCalled(); expect(error).toHaveBeenCalledWith(expect.stringContaining('麦克风已断开'), undefined);
  });
  it('captures during initialization and drains opening audio before finish after early release', async () => {
    let ready!: (value: any) => void;
    (connectRealtimeAsr as jest.Mock).mockReturnValue(new Promise(resolve => { ready = resolve; }));
    const handle = await createVoiceInputAdapter().start({});
    port.onmessage({ data: new Float32Array([0.25, 0.5]) }); handle.stop();
    await waitFor(() => expect(track.stop).toHaveBeenCalled());
    expect(connection.sendAudio).not.toHaveBeenCalled(); ready(connection);
    await waitFor(() => expect(connection.finish).toHaveBeenCalledTimes(1));
    expect(Array.from(new Int16Array(connection.sendAudio.mock.calls[0][0]))).toEqual([8191, 16383]);
  });
});
