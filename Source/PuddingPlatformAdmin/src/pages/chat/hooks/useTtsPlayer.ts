import { useCallback, useRef, useState } from 'react';
import { synthesizeTts } from '@/services/platform/api';

/**
 * TTS 播放 Hook — 将文本发送到后端合成语音并自动播放。
 * 每次调用 speak() 会停止前一次播放。
 * 通过 AbortController + pending 标志防止并发重复播放。
 */
export function useTtsPlayer() {
  const [playing, setPlaying] = useState(false);
  const [loading, setLoading] = useState(false);
  const audioRef = useRef<HTMLAudioElement | null>(null);
  const abortRef = useRef<AbortController | null>(null);
  const pendingRef = useRef(false);

  const cleanupAudio = useCallback(() => {
    if (audioRef.current) {
      audioRef.current.pause();
      URL.revokeObjectURL(audioRef.current.src);
      audioRef.current = null;
    }
  }, []);

  const speak = useCallback(
    async (text: string, voice?: string) => {
      // 防止并发：如果已有正在进行的合成请求，先中止它
      if (abortRef.current) {
        abortRef.current.abort();
      }
      // 停止前一次播放并清理
      cleanupAudio();
      // 标记 pending，防止并发调用进入
      if (pendingRef.current) {
        return;
      }
      pendingRef.current = true;

      const ctrl = new AbortController();
      abortRef.current = ctrl;
      setLoading(true);

      try {
        const blob = await synthesizeTts({ text, voice, format: 'wav' });
        // 在 fetch 期间可能已被新的 speak() 中止
        if (ctrl.signal.aborted) return;

        const url = URL.createObjectURL(blob);
        const audio = new Audio(url);
        audioRef.current = audio;

        audio.onended = () => {
          setPlaying(false);
          URL.revokeObjectURL(url);
          audioRef.current = null;
        };

        audio.onerror = () => {
          setPlaying(false);
          URL.revokeObjectURL(url);
          audioRef.current = null;
        };

        await audio.play();
        if (ctrl.signal.aborted) {
          audio.pause();
          URL.revokeObjectURL(url);
          audioRef.current = null;
          return;
        }
        setPlaying(true);
      } catch (err) {
        if ((err as Error)?.name === 'AbortError' || ctrl.signal.aborted)
          return;
        console.error('TTS synthesis failed:', err);
      } finally {
        if (abortRef.current === ctrl) {
          abortRef.current = null;
        }
        pendingRef.current = false;
        setLoading(false);
      }
    },
    [cleanupAudio],
  );

  const stop = useCallback(() => {
    if (abortRef.current) {
      abortRef.current.abort();
      abortRef.current = null;
    }
    cleanupAudio();
    pendingRef.current = false;
    setPlaying(false);
    setLoading(false);
  }, [cleanupAudio]);

  return { speak, stop, playing, loading };
}
