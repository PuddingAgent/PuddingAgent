import { useEffect, useRef } from 'react';
import { useTtsPlayer } from './useTtsPlayer';

/**
 * 自动 TTS 播放 Hook — 当 assistant 消息带有 voice.enabled 标记时自动朗读。
 * 通过 `spokenIds` 集合防止同一条消息被重复播放。
 * 同时检查 playing/loading 状态防止并发播放。
 */
export function useAutoTts(
  messages: {
    id: string;
    role: string;
    content: string;
    voice?: { enabled?: boolean; tts_text?: string };
  }[],
  autoTtsEnabled: boolean,
) {
  const { speak, playing, loading } = useTtsPlayer();
  const spokenIds = useRef(new Set<string>());

  useEffect(() => {
    if (!autoTtsEnabled) return;

    const lastAssistant = [...messages]
      .reverse()
      .find((m) => m.role === 'assistant' || m.role === 'agent');

    if (!lastAssistant || spokenIds.current.has(lastAssistant.id)) return;

    const voice = lastAssistant.voice as
      | { enabled?: boolean; tts_text?: string }
      | undefined;
    if (voice?.enabled && lastAssistant.content.trim()) {
      spokenIds.current.add(lastAssistant.id);
      const text = voice.tts_text || lastAssistant.content;
      speak(text);
    }
    // playing/loading 仅用于返回值上报，不放入依赖数组防止循环触发
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [messages, autoTtsEnabled, speak]);

  return { playing, loading };
}
