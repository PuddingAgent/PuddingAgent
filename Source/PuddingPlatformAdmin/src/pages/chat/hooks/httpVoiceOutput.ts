import type {
  BrowserVoiceOutputAdapter,
  BrowserVoiceOutputHandlers,
  BrowserVoiceOutputHandle,
} from './browserVoiceOutput';

/**
 * 后端 TTS 适配器 —— 把 ```voice 围栏接到 DashScope Qwen3-TTS。
 *
 * 为什么不再用 `browserVoiceOutput`：那是 `speechSynthesis`，音色由操作系统
 * 决定（Windows 中文普遍机械）。这里的音色来自 `D:\Data\config\voice\providers.json`
 * 已注册的 Qwen 音色（Cherry / Stella / Serena），24 kHz 自然语音。
 *
 * ## 四条约束
 *
 * 1. **契约与 browserVoiceOutput 完全一致** —— 调用方只换注入的实现。
 * 2. **失败不回退** —— 后端出错就报错。静默换回系统音色会让用户以为
 *    “音色变了”，实际只是把失败藏起来。
 * 3. **首次播放需要用户手势** —— 浏览器 autoplay 策略会拦自动播放，
 *    这时把错误交给 handler，由 UI 提示点击播放。
 * 4. **stop 必须同时中止请求与音频** —— 否则切换消息后合成请求还在跑。
 *
 * 接口契约见 `Source/PuddingPlatform/Controllers/Api/VoiceController.cs`：
 * `POST /api/voice/tts/synthesize`（`[Authorize]`）→ audio/wav。
 */
export interface HttpVoiceOutputOptions {
  /** 合成接口，默认 `/api/voice/tts/synthesize`。 */
  endpoint?: string;
  /** 音色名，必须来自 providers.json 的 ttsModels[].voices。 */
  voice?: string;
  /** 采样率，默认 24000（与 providers.json 一致）。 */
  sampleRate?: number;
  /** 供测试注入；默认用全局 fetch。 */
  fetchImpl?: typeof fetch;
  /** 供测试注入；默认读 localStorage 的 pudding_token。 */
  getToken?: () => string | null;
  /** 供测试注入；默认 new Audio()。 */
  createAudio?: () => HTMLAudioElement;
  /** 供测试注入；默认 URL.createObjectURL。 */
  createObjectUrl?: (blob: Blob) => string;
  /** 供测试注入；默认 URL.revokeObjectURL。 */
  revokeObjectUrl?: (url: string) => void;
}

/** 前端 token 的存储键（与 src/app.tsx 保持一致）。 */
export const VOICE_TOKEN_STORAGE_KEY = 'pudding_token';

/** 单个音色的展示信息，顺序即下拉顺序。 */
export interface VoiceOption {
  id: string;
  label: string;
}

/** providers.json 中已注册的音色。改动配置时这里要同步。 */
export const voiceOptions: VoiceOption[] = [
  { id: 'Cherry', label: 'Cherry · 亲切女声' },
  { id: 'Stella', label: 'Stella · 沉稳女声' },
  { id: 'Serena', label: 'Serena · 温柔女声' },
];

/** 用户选定音色的存储键。 */
export const VOICE_NAME_STORAGE_KEY = 'pudding_voice_name';

export const defaultVoiceName = 'Cherry';

/** 读取用户选定的音色；非法值回落到默认，避免把脏数据发给后端。 */
export const readStoredVoiceName = (): string => {
  try {
    const stored = globalThis.localStorage?.getItem(VOICE_NAME_STORAGE_KEY);
    if (stored && voiceOptions.some((option) => option.id === stored)) {
      return stored;
    }
  } catch {
    // localStorage 在隐私模式下会抛；按默认音色处理即可。
  }
  return defaultVoiceName;
};

export const writeStoredVoiceName = (voice: string): void => {
  try {
    globalThis.localStorage?.setItem(VOICE_NAME_STORAGE_KEY, voice);
  } catch {
    // 存不进去只影响下次默认值，不影响本次播放。
  }
};

const readToken = (): string | null => {
  try {
    return globalThis.localStorage?.getItem(VOICE_TOKEN_STORAGE_KEY) ?? null;
  } catch {
    return null;
  }
};

/**
 * 从失败响应里取可读信息。
 *
 * 后端两种形态都要照顾：`[VoiceFailure]` 的 `{code,message}` 与
 * ASP.NET `ProblemDetails` 的 `{detail,title}`。非 JSON 只截断原文，
 * 不把整页 HTML 塞进 UI。
 */
export const describeVoiceFailure = async (
  response: Response,
): Promise<string> => {
  const fallback = `语音合成失败（HTTP ${response.status}）`;
  try {
    const raw = await response.text();
    if (!raw) return fallback;
    try {
      const parsed = JSON.parse(raw) as {
        message?: string;
        detail?: string;
        error?: { message?: string };
      };
      const message = parsed.message ?? parsed.detail ?? parsed.error?.message;
      if (message) return message;
    } catch {
      // 非 JSON：落到截断原文。
    }
    return raw.slice(0, 200);
  } catch {
    return fallback;
  }
};

export const createHttpVoiceOutputAdapter = (
  options: HttpVoiceOutputOptions = {},
): BrowserVoiceOutputAdapter => {
  const endpoint = options.endpoint ?? '/api/voice/tts/synthesize';
  const sampleRate = options.sampleRate ?? 24000;
  const doFetch =
    options.fetchImpl ?? ((input, init) => globalThis.fetch(input, init));
  const getToken = options.getToken ?? readToken;
  const createAudio =
    options.createAudio ?? (() => new Audio() as HTMLAudioElement);
  const createObjectUrl =
    options.createObjectUrl ?? ((blob: Blob) => URL.createObjectURL(blob));
  const revokeObjectUrl =
    options.revokeObjectUrl ?? ((url: string) => URL.revokeObjectURL(url));

  // 同一时刻只允许一段朗读：新的 speak 会先中止上一段。
  let active: { abort: () => void } | null = null;

  return {
    isSupported: () =>
      typeof doFetch === 'function' && typeof createAudio === 'function',

    speak(
      text: string,
      handlers: BrowserVoiceOutputHandlers = {},
    ): BrowserVoiceOutputHandle {
      const trimmed = (text ?? '').trim();

      // 互斥：先停掉上一段，再开始新的。
      active?.abort();
      active = null;

      if (!trimmed) {
        handlers.onComplete?.();
        return { stop: () => undefined };
      }

      const controller = new AbortController();
      let stopped = false;
      let audio: HTMLAudioElement | null = null;
      let objectUrl: string | null = null;

      const releaseUrl = () => {
        if (objectUrl) {
          revokeObjectUrl(objectUrl);
          objectUrl = null;
        }
      };

      const stop = () => {
        stopped = true;
        controller.abort();
        try {
          audio?.pause();
        } catch {
          // 元素可能还没挂上 src；暂停失败不影响后续清理。
        }
        releaseUrl();
        if (active?.abort === stop) active = null;
      };

      active = { abort: stop };

      void (async () => {
        const token = getToken();
        const headers: Record<string, string> = {
          'content-type': 'application/json',
        };
        // 接口带 [Authorize]：没 token 会 401，这里如实报错而不是静默降级。
        if (token) headers.authorization = `Bearer ${token}`;

        const response = await doFetch(endpoint, {
          method: 'POST',
          headers,
          signal: controller.signal,
          body: JSON.stringify({
            text: trimmed,
            voice: options.voice ?? defaultVoiceName,
            format: 'wav',
            sampleRate,
          }),
        });

        if (!response.ok) {
          throw new Error(await describeVoiceFailure(response));
        }

        const blob = await response.blob();
        if (stopped) return;

        objectUrl = createObjectUrl(blob);
        const element = createAudio();
        element.src = objectUrl;
        audio = element;
        element.onended = () => {
          releaseUrl();
          if (active?.abort === stop) active = null;
          handlers.onComplete?.();
        };
        element.onerror = () => {
          releaseUrl();
          if (active?.abort === stop) active = null;
          handlers.onError?.('音频播放失败');
        };

        // play() 返回 Promise：autoplay 被拦截时会 reject，交给 catch 报错。
        await element.play();
        if (!stopped) handlers.onStart?.();
      })().catch((reason: unknown) => {
        if (stopped) return; // 用户主动停止不算错误。
        if (active?.abort === stop) active = null;
        releaseUrl();
        const message =
          reason instanceof Error
            ? reason.message
            : String(reason ?? '语音朗读失败');
        handlers.onError?.(message);
      });

      return { stop };
    },
  };
};
