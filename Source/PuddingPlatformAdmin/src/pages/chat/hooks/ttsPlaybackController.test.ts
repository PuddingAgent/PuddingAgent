import { createTtsPlaybackController } from './ttsPlaybackController';

jest.mock('@/services/platform/api', () => ({ streamSpeech: jest.fn() }));

const audioContext = () => ({ state: 'running', currentTime: 0, resume: jest.fn().mockResolvedValue(undefined),
  close: jest.fn().mockResolvedValue(undefined), destination: {},
  createBuffer: (_channels: number, samples: number, rate: number) => ({ duration: samples / rate, copyToChannel: jest.fn() }),
  createBufferSource: () => ({ connect: jest.fn(), start: jest.fn() }),
}) as any;

describe('shared TTS playback lifecycle', () => {
  it('switching owner aborts the previous network request and closes its audio', async () => {
    const signals: AbortSignal[] = [];
    const contexts: any[] = [];
    async function* stream(_text: string, signal: AbortSignal) {
      signals.push(signal);
      await new Promise<void>((resolve) => signal.addEventListener('abort', () => resolve(), { once: true }));
      if (false) yield { data: '', sampleRate: 24000 };
    }
    const manager = createTtsPlaybackController(stream, () => { const context = audioContext(); contexts.push(context); return context; });
    const first = manager.speak('first', 'hello'); await Promise.resolve();
    const second = manager.speak('second', 'world'); await Promise.resolve();
    expect(signals[0].aborted).toBe(true);
    expect(contexts[0].close).toHaveBeenCalled();
    manager.stop('first'); expect(manager.getSnapshot().owner).toBe('second');
    manager.stop('second'); await Promise.all([first, second]);
    expect(signals[1].aborted).toBe(true);
  });
  it('surfaces quota errors and releases audio resources', async () => {
    const context = audioContext();
    async function* stream() { throw new Error('语音额度不足'); if (false) yield { data: '', sampleRate: 24000 }; }
    const manager = createTtsPlaybackController(stream, () => context);
    await manager.speak('message', 'hello');
    expect(manager.getSnapshot()).toMatchObject({ status: 'failed', error: '语音额度不足' });
    expect(context.close).toHaveBeenCalled();
  });
  it('handles PCM split in the middle of a sample and waits for completion', async () => {
    async function* stream() { yield { data: 'AQ==', sampleRate: 24000 }; yield { data: 'Ag==', sampleRate: 24000 }; }
    const manager = createTtsPlaybackController(stream, audioContext);
    await manager.speak('message', 'hello');
    expect(manager.getSnapshot().status).toBe('playing');
    manager.stop();
  });
});
