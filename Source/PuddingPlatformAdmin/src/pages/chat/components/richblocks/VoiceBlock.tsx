import React, { useCallback, useEffect, useRef, useState } from 'react';
import type { RichBlockProps } from './RichBlockRegistry';
import {
  defaultBrowserVoiceOutputAdapter,
  type BrowserVoiceOutputAdapter,
  type BrowserVoiceOutputHandle,
} from '../../hooks/browserVoiceOutput';

/**
 * ```voice 围栏渲染器 —— 让回复在**对话里**可以出声。
 *
 * 之前 `voice` 围栏只有飞书通道会投影成语音消息（HasVoiceDirective），
 * 网页/桌面聊天里它退化成一个普通代码块：模型“说了话”，用户只看到一段代码。
 * 这个组件补的就是网页侧的出口。
 *
 * ## 三条约束
 *
 * 1. **复用已有适配器**：`browserVoiceOutput` 已封装 speechSynthesis 的
 *    speak/stop/onError，这里不再写第二套。
 * 2. **适配器可注入**：默认用浏览器实现，测试传假适配器——
 *    否则断言只能证明“浏览器支持朗读”，证明不了本组件的逻辑。
 * 3. **卸载即停**：切换消息或会话时不能继续念上一段。
 */
export interface VoiceBlockProps extends RichBlockProps {
  adapter?: BrowserVoiceOutputAdapter;
  /** 围栏是模型的显式意图，默认直接朗读；用户仍可点按钮停止/重播。 */
  autoPlay?: boolean;
  lang?: string;
}

export const VoiceBlock: React.FC<VoiceBlockProps> = ({
  code,
  adapter = defaultBrowserVoiceOutputAdapter,
  autoPlay = true,
  lang = 'zh-CN',
}) => {
  const text = (code ?? '').trim();
  const handleRef = useRef<BrowserVoiceOutputHandle | null>(null);
  const [playing, setPlaying] = useState(false);
  const [error, setError] = useState<string>();

  const stop = useCallback(() => {
    handleRef.current?.stop();
    handleRef.current = null;
    setPlaying(false);
  }, []);

  const speak = useCallback(() => {
    if (!text) return;
    if (!adapter.isSupported()) {
      setError('当前浏览器不支持语音朗读');
      setPlaying(false);
      return;
    }
    stop();
    setError(undefined);
    try {
      handleRef.current = adapter.speak(text, {
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
  }, [adapter, lang, stop, text]);

  useEffect(() => {
    if (autoPlay) speak();
    return () => {
      handleRef.current?.stop();
      handleRef.current = null;
    };
  }, [autoPlay, speak]);

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
        onClick={() => (playing ? stop() : speak())}
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
          fontSize: 12,
          color: error ? '#f53f3f' : '#4e5969',
          whiteSpace: 'pre-wrap',
          wordBreak: 'break-word',
        }}
      >
        {error ?? text}
      </span>
    </div>
  );
};

export default VoiceBlock;
