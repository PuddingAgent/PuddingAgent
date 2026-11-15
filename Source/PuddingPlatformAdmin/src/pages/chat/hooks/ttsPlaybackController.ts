import { streamSpeech } from '@/services/platform/api';

export type PlaybackSnapshot = { owner?: string; status: 'idle' | 'loading' | 'playing' | 'failed'; error?: string };
type SpeechStream = (text: string, signal: AbortSignal) => AsyncIterable<{ data: string; sampleRate: number }>;

export function createTtsPlaybackController(stream: SpeechStream = streamSpeech, createContext = () => new AudioContext({ sampleRate: 24000 })) {
  let snapshot: PlaybackSnapshot = { status: 'idle' };
  let controller: AbortController | undefined;
  let context: AudioContext | undefined;
  let completionTimer: ReturnType<typeof setTimeout> | undefined;
  const listeners = new Set<() => void>();
  const publish = (next: PlaybackSnapshot) => { snapshot = next; listeners.forEach((listener) => listener()); };
  const stop = (owner?: string) => {
    if (owner && snapshot.owner !== owner) return;
    controller?.abort(); controller = undefined;
    clearTimeout(completionTimer);
    if (context && context.state !== 'closed') void context.close().catch(() => {});
    context = undefined;
    publish({ status: 'idle' });
  };
  return {
    subscribe: (listener: () => void) => { listeners.add(listener); return () => { listeners.delete(listener); }; },
    getSnapshot: () => snapshot,
    stop,
    async speak(owner: string, rawText: string) {
      stop();
      const text = rawText.replace(/```[\s\S]*?```/g, '').replace(/https?:\/\/\S+/g, '').replace(/\[([^\]]+)\]\([^)]+\)/g, '$1').trim();
      if (!text) { publish({ owner, status: 'failed', error: '这条消息没有适合朗读的文字。' }); return; }
      const request = new AbortController(); controller = request;
      publish({ owner, status: 'loading' });
      let audioContext: AudioContext;
      try { audioContext = createContext(); context = audioContext; await audioContext.resume(); }
      catch { if (!request.signal.aborted) { stop(owner); publish({ owner, status: 'failed', error: '无法启动音频播放，请检查声音设备和播放权限。' }); } return; }
      if (request.signal.aborted) return;
      publish({ owner, status: 'loading' });
      let nextTime = audioContext.currentTime;
      let rest = new Uint8Array(0);
      let hasAudio = false;
      try {
        for await (const chunk of stream(text, request.signal)) {
          if (request.signal.aborted) return;
          const decoded = Uint8Array.from(atob(chunk.data), (value) => value.charCodeAt(0));
          const bytes = new Uint8Array(rest.length + decoded.length); bytes.set(rest); bytes.set(decoded, rest.length);
          const count = Math.floor(bytes.length / 2); rest = bytes.slice(count * 2);
          if (!count) continue;
          if (chunk.sampleRate !== 24000) throw new Error('语音采样格式不支持，请检查模型设置。');
          const samples = new Float32Array(count);
          const view = new DataView(bytes.buffer);
          for (let i = 0; i < count; i++) samples[i] = view.getInt16(i * 2, true) / 32768;
          const buffer = audioContext.createBuffer(1, count, chunk.sampleRate); buffer.copyToChannel(samples, 0);
          const source = audioContext.createBufferSource(); source.buffer = buffer; source.connect(audioContext.destination);
          nextTime = Math.max(nextTime, audioContext.currentTime + 0.03); source.start(nextTime); nextTime += buffer.duration;
          hasAudio = true; publish({ owner, status: 'playing' });
          while (nextTime - audioContext.currentTime > 2 && !request.signal.aborted)
            await new Promise((resolve) => setTimeout(resolve, 50));
        }
        if (!hasAudio || rest.length) throw new Error('语音响应不完整，请重试。');
        if (request.signal.aborted) return;
        completionTimer = setTimeout(() => { if (controller === request) stop(owner); }, Math.max(0, nextTime - audioContext.currentTime) * 1000);
      } catch (error) {
        if (request.signal.aborted) return;
        stop(owner);
        publish({ owner, status: 'failed', error: error instanceof Error ? error.message : '语音朗读失败，请重试。' });
      }
    },
  };
}

export const ttsPlayback = createTtsPlaybackController();
