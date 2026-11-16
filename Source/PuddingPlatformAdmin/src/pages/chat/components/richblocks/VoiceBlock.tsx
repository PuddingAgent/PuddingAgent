import React, {
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useRef,
  useState,
} from 'react';
import type { RichBlockProps } from './RichBlockRegistry';
import { VoiceAutoplayContext } from './VoiceAutoplayContext';
import type {
  BrowserVoiceOutputAdapter,
  BrowserVoiceOutputHandle,
} from '../../hooks/browserVoiceOutput';
import {
  createHttpVoiceOutputAdapter,
  readStoredVoiceName,
  voiceOptions,
  writeStoredVoiceName,
} from '../../hooks/httpVoiceOutput';

/**
 * ```voice 围栏渲染器 —— 让回复在**对话里**可以出声。
 *
 * 之前 `voice` 围栏只有飞书通道会投影成语音消息（HasVoiceDirective），
 * 网页/桌面聊天里它退化成一个普通代码块：模型“说了话”，用户只看到一段代码。
 * 这个组件补的就是网页侧的出口。
 *
 * ## 四条约束
 *
 * 1. **默认走后端 Qwen3-TTS**：`speechSynthesis` 的音色由操作系统决定，
 *    Windows 中文机械得不能听。改用 `httpVoiceOutput`，音色来自
 *    `D:\Data\config\voice\providers.json` 注册的 Cherry / Stella / Vera。
 * 2. **适配器可注入**：测试传假适配器——否则断言只能证明“浏览器支持朗读”，
 *    证明不了本组件的逻辑。
 * 3. **卸载即停**：切换消息或会话时不能继续念上一段。
 * 4. **失败不回退**：合成失败就显式报错。静默换回系统音色会让人误以为
 *    “音色变了”，实际是把失败藏起来。
 */
export interface VoiceBlockProps extends RichBlockProps {
  adapter?: BrowserVoiceOutputAdapter;
  /** 默认自动朗读一次；聊天消息还受生成完成/历史消息策略约束。 */
  autoPlay?: boolean;
  lang?: string;
  /** 是否显示音色下拉；调成 false 可只保留播放按钮。 */
  showVoicePicker?: boolean;
}

export const VoiceBlock: React.FC<VoiceBlockProps> = ({
  code,
  adapter,
  autoPlay = true,
  lang = 'zh-CN',
  showVoicePicker = true,
}) => {
  const text = (code ?? '').trim();
  const handleRef = useRef<BrowserVoiceOutputHandle | null>(null);
  const autoPlayedRef = useRef(false);
  const autoplayPolicy = useContext(VoiceAutoplayContext);
  const [playing, setPlaying] = useState(false);
  const [error, setError] = useState<string>();
  const [voiceName, setVoiceName] = useState<string>(() =>
    readStoredVoiceName(),
  );

  // 音色变化只影响下一次手动播放，不触发重播。
  const builtAdapter = useMemo(
    () => createHttpVoiceOutputAdapter({ voice: voiceName }),
    [voiceName],
  );
  const activeAdapter = adapter ?? builtAdapter;

  const stop = useCallback(() => {
    handleRef.current?.stop();
    handleRef.current = null;
    setPlaying(false);
  }, []);

  const speak = useCallback(() => {
    if (!text) return;
    if (!activeAdapter.isSupported()) {
      setError('当前浏览器不支持语音朗读');
      setPlaying(false);
      return;
    }
    stop();
    setError(undefined);
    try {
      handleRef.current = activeAdapter.speak(text, {
        lang,
        onStart: () => setPlaying(true),
        onComplete: () => {
          handleRef.current = null;
          setPlaying(false);
        },
        onError: (message) => {
          handleRef.current = null;
          setPlaying(false);
          setError(message);
        },
      });
    } catch (reason) {
      setError((reason as Error).message);
      setPlaying(false);
    }
  }, [activeAdapter, lang, stop, text]);

  useEffect(() => {
    if (!autoPlay || !text || (autoplayPolicy && !autoplayPolicy.allowAutoPlay))
      return;
    let cancelled = false;
    // StrictMode 的首次 effect 清理可以取消启动，避免先播放又立刻被清理停止。
    queueMicrotask(() => {
      if (cancelled) return;
      if (autoPlayedRef.current) return;
      if (autoplayPolicy && !autoplayPolicy.claim(text)) return;
      // 自动机会在调用适配器前消耗；合成失败也只允许手动重试。
      autoPlayedRef.current = true;
      speak();
    });
    return () => {
      cancelled = true;
    };
  }, [autoPlay, autoplayPolicy, speak, text]);

  useEffect(() => {
    return () => {
      handleRef.current?.stop();
      handleRef.current = null;
    };
  }, []);

  const handleVoiceChange = useCallback(
    (event: React.ChangeEvent<HTMLSelectElement>) => {
      const next = event.target.value;
      writeStoredVoiceName(next);
      setVoiceName(next);
    },
    [],
  );

  if (!text) return null;

  return (
    <div
      data-testid="voice-block"
      style={{
        display: 'flex',
        alignItems: 'center',
        gap: 10,
        padding: '8px 12px',
        border: '1px solid #e5e6eb',
        borderRadius: 8,
        background: '#f7f8fa',
      }}
    >
      <button
        type="button"
        data-testid="voice-block-toggle"
        aria-label={playing ? '停止朗读' : '朗读这段'}
        onClick={() => {
          // 手动操作取消待执行的自动机会，但手动重播不受自动守卫限制。
          autoPlayedRef.current = true;
          autoplayPolicy?.onManualControl();
          if (playing) stop();
          else speak();
        }}
        style={{
          flex: '0 0 auto',
          width: 28,
          height: 28,
          borderRadius: 14,
          border: 'none',
          cursor: 'pointer',
          background: playing ? '#8b5cf6' : '#1f2329',
          color: '#fff',
          lineHeight: '28px',
          padding: 0,
        }}
      >
        {playing ? '■' : '▶'}
      </button>
      <span
        style={{
          flex: '1 1 auto',
          fontSize: 12,
          color: error ? '#f53f3f' : '#4e5969',
          whiteSpace: 'pre-wrap',
          wordBreak: 'break-word',
        }}
      >
        {error ?? text}
      </span>
      {showVoicePicker ? (
        <select
          data-testid="voice-block-voice"
          aria-label="音色"
          value={voiceName}
          onChange={handleVoiceChange}
          style={{
            flex: '0 0 auto',
            fontSize: 12,
            color: '#4e5969',
            border: '1px solid #e5e6eb',
            borderRadius: 6,
            background: '#fff',
            padding: '2px 4px',
            cursor: 'pointer',
          }}
        >
          {voiceOptions.map((option) => (
            <option key={option.id} value={option.id}>
              {option.label}
            </option>
          ))}
        </select>
      ) : null}
    </div>
  );
};

export default VoiceBlock;
