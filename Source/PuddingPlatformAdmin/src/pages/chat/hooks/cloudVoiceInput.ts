import { recognizeAsr } from '@/services/platform/api';
import type { BrowserVoiceInputAdapter } from './browserVoiceInput';
import { encodeWave, MAX_RECORDING_SECONDS, RECORDING_SAMPLE_RATE } from './voiceAudio';

/** Provider-neutral capture. Only Core knows which recognition model receives the audio. */
export function createVoiceInputAdapter(): BrowserVoiceInputAdapter {
  return {
    isSupported: () => typeof globalThis.navigator?.mediaDevices?.getUserMedia === 'function'
      && typeof globalThis.AudioContext === 'function' && typeof globalThis.AudioWorkletNode === 'function',
    async start(handlers) {
      const stream = await navigator.mediaDevices.getUserMedia({ audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true } });
      const controller = new AbortController();
      const chunks: Float32Array[] = [];
      let context: AudioContext | undefined;
      let source: MediaStreamAudioSourceNode | undefined;
      let node: AudioWorkletNode | undefined;
      let cancelled = false;
      let finishing = false;
      let samples = 0;
      let finishResolve: (() => void) | undefined;
      const release = () => {
        stream.getTracks().forEach((track) => track.stop());
        source?.disconnect(); node?.disconnect();
        if (context && context.state !== 'closed') void context.close().catch(() => {});
      };
      const cancel = () => {
        cancelled = true; controller.abort(); finishResolve?.(); chunks.length = 0; release();
      };
      const stop = () => {
        if (finishing || cancelled) return;
        finishing = true;
        handlers.onTranscribing?.();
        void (async () => {
          try {
            await new Promise<void>((resolve) => { finishResolve = resolve; node!.port.postMessage('finish'); });
            release();
            if (cancelled) return;
            if (!samples) throw new Error('没有录到声音，请靠近麦克风再试。');
            const result = await recognizeAsr(encodeWave(chunks), controller.signal);
            if (!cancelled) handlers.onFinalTranscript?.(result.text);
          } catch (error) {
            if (!cancelled) handlers.onError?.(error instanceof Error ? error.message : '语音识别失败，请重试。');
          } finally { chunks.length = 0; release(); }
        })();
      };
      try {
        context = new AudioContext({ sampleRate: RECORDING_SAMPLE_RATE });
        if (context.sampleRate !== RECORDING_SAMPLE_RATE) throw new Error('当前设备不支持 16 kHz 录音，请更换麦克风。');
        await context.resume();
        const base = window.location.pathname.startsWith('/admin') ? '/admin/' : '/';
        await context.audioWorklet.addModule(`${base}audio/pcm-capture.js`);
        source = context.createMediaStreamSource(stream);
        node = new AudioWorkletNode(context, 'pudding-pcm-capture');
        node.port.onmessage = ({ data }) => {
          if (cancelled) return;
          if (data === 'finished') { finishResolve?.(); return; }
          const chunk = data as Float32Array;
          const allowed = MAX_RECORDING_SECONDS * RECORDING_SAMPLE_RATE - samples;
          const bounded = chunk.subarray(0, Math.max(0, allowed));
          chunks.push(bounded); samples += bounded.length;
          let energy = 0;
          for (const sample of bounded) energy += sample * sample;
          handlers.onLevel?.(Math.min(1, Math.sqrt(energy / Math.max(1, bounded.length)) * 4), samples / RECORDING_SAMPLE_RATE);
          if (samples >= MAX_RECORDING_SECONDS * RECORDING_SAMPLE_RATE) stop();
        };
        source.connect(node);
        // The processor emits silence; connection keeps capture active in WebView2.
        node.connect(context.destination);
        stream.getAudioTracks().forEach((track) => { track.onended = () => { if (!finishing && !cancelled) { cancel(); handlers.onError?.('麦克风已断开，请重新录音。'); } }; });
        handlers.onPermissionGranted?.(stream.getAudioTracks()[0]?.label);
        return { stop, cancel };
      } catch (error) {
        cancel();
        throw new Error(error instanceof DOMException && error.name === 'NotAllowedError'
          ? '麦克风权限被拒绝，请在系统和应用设置中允许录音。'
          : error instanceof Error ? error.message : '无法启动录音。');
      }
    },
  };
}
