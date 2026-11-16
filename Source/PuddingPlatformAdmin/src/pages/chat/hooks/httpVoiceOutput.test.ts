import {
  createHttpVoiceOutputAdapter,
  describeVoiceFailure,
  defaultVoiceName,
  readStoredVoiceName,
  VOICE_NAME_STORAGE_KEY,
  voiceOptions,
} from './httpVoiceOutput';

/** 让 speak 内部的 async 链走完一帧。 */
const flush = async () => {
  await Promise.resolve();
  await Promise.resolve();
  await new Promise((resolve) => setTimeout(resolve, 0));
};

interface FakeAudio {
  src: string;
  play: jest.Mock;
  pause: jest.Mock;
  onended: (() => void) | null;
  onerror: (() => void) | null;
}

const makeAudio = (): FakeAudio => ({
  src: '',
  play: jest.fn(() => Promise.resolve()),
  pause: jest.fn(),
  onended: null,
  onerror: null,
});

/** 成功响应：返回一个可被 audio 播放的 Blob。 */
const okResponse = () =>
  ({
    ok: true,
    status: 200,
    blob: async () => new Blob(['RIFF'], { type: 'audio/wav' }),
  }) as unknown as Response;

describe('httpVoiceOutput', () => {
  it('把文本、音色与采样率发给后端，并把返回音频交给 audio 播放', async () => {
    const audio = makeAudio();
    const fetchImpl = jest.fn(async () => okResponse());
    const onStart = jest.fn();
    const adapter = createHttpVoiceOutputAdapter({
      voice: 'Stella',
      fetchImpl: fetchImpl as unknown as typeof fetch,
      getToken: () => 'tk-1',
      createAudio: () => audio as unknown as HTMLAudioElement,
      createObjectUrl: () => 'blob:voice',
      revokeObjectUrl: jest.fn(),
    });

    adapter.speak('今天先睡吧', { onStart });
    await flush();

    expect(fetchImpl).toHaveBeenCalledTimes(1);
    const [url, init] = fetchImpl.mock.calls[0] as unknown as [
      string,
      RequestInit,
    ];
    expect(url).toBe('/api/voice/tts/synthesize');
    expect(init.method).toBe('POST');
    expect((init.headers as Record<string, string>).authorization).toBe(
      'Bearer tk-1',
    );
    expect(JSON.parse(String(init.body))).toEqual({
      text: '今天先睡吧',
      voice: 'Stella',
      format: 'wav',
      sampleRate: 24000,
    });
    expect(audio.src).toBe('blob:voice');
    expect(audio.play).toHaveBeenCalledTimes(1);
    expect(onStart).toHaveBeenCalledTimes(1);
  });

  it('没有 token 时不伪造 Authorization 头', async () => {
    const fetchImpl = jest.fn(async () => okResponse());
    const adapter = createHttpVoiceOutputAdapter({
      fetchImpl: fetchImpl as unknown as typeof fetch,
      getToken: () => null,
      createAudio: () => makeAudio() as unknown as HTMLAudioElement,
      createObjectUrl: () => 'blob:x',
      revokeObjectUrl: jest.fn(),
    });

    adapter.speak('测试');
    await flush();

    const [, init] = fetchImpl.mock.calls[0] as unknown as [string, RequestInit];
    expect((init.headers as Record<string, string>).authorization).toBeUndefined();
  });

  it('后端失败时报错，且不回退到浏览器音色', async () => {
    const onError = jest.fn();
    const createAudio = jest.fn(() => makeAudio() as unknown as HTMLAudioElement);
    const fetchImpl = jest.fn(
      async () =>
        ({
          ok: false,
          status: 502,
          text: async () => JSON.stringify({ message: '余额不足' }),
        }) as unknown as Response,
    );
    const adapter = createHttpVoiceOutputAdapter({
      fetchImpl: fetchImpl as unknown as typeof fetch,
      getToken: () => 'tk',
      createAudio,
      createObjectUrl: () => 'blob:x',
      revokeObjectUrl: jest.fn(),
    });

    adapter.speak('念不出来', { onError });
    await flush();

    expect(onError).toHaveBeenCalledWith('余额不足');
    // 不回退的关键断言：失败时绝不创建播放器。
    expect(createAudio).not.toHaveBeenCalled();
  });

  it('stop 会中止请求并暂停音频', async () => {
    const audio = makeAudio();
    let capturedSignal: AbortSignal | undefined;
    const fetchImpl = jest.fn(async (_url: string, init: RequestInit) => {
      capturedSignal = init.signal ?? undefined;
      return okResponse();
    });
    const adapter = createHttpVoiceOutputAdapter({
      fetchImpl: fetchImpl as unknown as typeof fetch,
      getToken: () => 'tk',
      createAudio: () => audio as unknown as HTMLAudioElement,
      createObjectUrl: () => 'blob:x',
      revokeObjectUrl: jest.fn(),
    });

    const handle = adapter.speak('停一下');
    await flush();
    handle.stop();

    expect(capturedSignal?.aborted).toBe(true);
    expect(audio.pause).toHaveBeenCalled();
  });

  it('新的 speak 会中止上一段，保证同一时刻只有一段朗读', async () => {
    const fetchImpl = jest.fn(async () => okResponse());
    const signals: AbortSignal[] = [];
    const adapter = createHttpVoiceOutputAdapter({
      fetchImpl: ((_url: string, init: RequestInit) => {
        if (init.signal) signals.push(init.signal);
        return (fetchImpl as unknown as (u: string, i: RequestInit) => Promise<Response>)(
          _url,
          init,
        );
      }) as unknown as typeof fetch,
      getToken: () => 'tk',
      createAudio: () => makeAudio() as unknown as HTMLAudioElement,
      createObjectUrl: () => 'blob:x',
      revokeObjectUrl: jest.fn(),
    });

    adapter.speak('第一段');
    adapter.speak('第二段');
    await flush();

    expect(signals.length).toBe(2);
    expect(signals[0].aborted).toBe(true);
    expect(signals[1].aborted).toBe(false);
  });

  it('空白文本不请求后端，直接结束', async () => {
    const fetchImpl = jest.fn(async () => okResponse());
    const onComplete = jest.fn();
    const adapter = createHttpVoiceOutputAdapter({
      fetchImpl: fetchImpl as unknown as typeof fetch,
      getToken: () => 'tk',
      createAudio: () => makeAudio() as unknown as HTMLAudioElement,
    });

    adapter.speak('   ', { onComplete });
    await flush();

    expect(fetchImpl).not.toHaveBeenCalled();
    expect(onComplete).toHaveBeenCalledTimes(1);
  });
});

describe('describeVoiceFailure', () => {
  it('识别 [VoiceFailure] 的 message 字段', async () => {
    const response = {
      status: 502,
      text: async () => JSON.stringify({ code: 'voice_provider_failed', message: '语音生成失败' }),
    } as unknown as Response;
    await expect(describeVoiceFailure(response)).resolves.toBe('语音生成失败');
  });

  it('识别 ProblemDetails 的 detail 字段', async () => {
    const response = {
      status: 502,
      text: async () => JSON.stringify({ title: 'Bad Gateway', detail: 'TTS returned no audio data.' }),
    } as unknown as Response;
    await expect(describeVoiceFailure(response)).resolves.toBe(
      'TTS returned no audio data.',
    );
  });

  it('非 JSON 响应只截断原文，不整页塞进 UI', async () => {
    const longHtml = '<html>' + 'x'.repeat(500);
    const response = {
      status: 500,
      text: async () => longHtml,
    } as unknown as Response;
    const message = await describeVoiceFailure(response);
    expect(message.length).toBeLessThanOrEqual(200);
  });
});

describe('音色记忆', () => {
  beforeEach(() => {
    localStorage.clear();
  });

  it('默认音色在已注册列表里', () => {
    expect(voiceOptions.map((option) => option.id)).toContain(defaultVoiceName);
    expect(readStoredVoiceName()).toBe(defaultVoiceName);
  });

  it('读取已保存的合法音色', () => {
    localStorage.setItem(VOICE_NAME_STORAGE_KEY, 'Serena');
    expect(readStoredVoiceName()).toBe('Serena');
  });

  it('已退役音色（Vera）不再被当成合法值，回落默认——上游对它报 400 InvalidParameter', () => {
    localStorage.setItem(VOICE_NAME_STORAGE_KEY, 'Vera');
    expect(voiceOptions.map((option) => option.id)).not.toContain('Vera');
    expect(readStoredVoiceName()).toBe(defaultVoiceName);
  });

  it('保存脏数据时回落到默认音色，避免把非法值发给后端', () => {
    localStorage.setItem(VOICE_NAME_STORAGE_KEY, 'NotAVoice');
    expect(readStoredVoiceName()).toBe(defaultVoiceName);
  });
});
