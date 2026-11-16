import { connectRealtimeAsr, type RealtimeAsrConnection } from '@/services/platform/realtimeAsr';
import type { BrowserVoiceInputAdapter } from './browserVoiceInput';
import { encodePcm16, MAX_RECORDING_SECONDS, RECORDING_SAMPLE_RATE } from './voiceAudio';

/** Capture and live transcription share one cancellable lifetime. No full recording is retained. */
export function createVoiceInputAdapter(): BrowserVoiceInputAdapter {
  return {
    isSupported: () => typeof globalThis.navigator?.mediaDevices?.getUserMedia === 'function'
      && typeof globalThis.AudioContext === 'function' && typeof globalThis.AudioWorkletNode === 'function',
    async start(handlers, signal) {
      const controller = new AbortController();
      let stream: MediaStream | undefined;
      let context: AudioContext | undefined;
      let source: MediaStreamAudioSourceNode | undefined;
      let node: AudioWorkletNode | undefined;
      let connection: RealtimeAsrConnection | undefined;
      let ended = false;
      let finishing = false;
      let samples = 0;
      let finishResolve: (() => void) | undefined;
      let flushTimer: ReturnType<typeof setTimeout> | undefined;
      const queued: ArrayBuffer[] = [];
      let queuedBytes = 0;
      let pumping: Promise<void> | undefined;
      let connected: Promise<void> = Promise.resolve();
      const pump = (): Promise<void> => {
        if (pumping) return pumping;
        if (!connection || !queued.length || ended) return Promise.resolve();
        pumping = (async () => {
          while (!ended && connection && queued.length) {
            const frame = queued.shift()!; queuedBytes -= frame.byteLength;
            connection.sendAudio(frame);
            // Drain initialization audio in bounded steps instead of flooding the server queue.
            await new Promise(resolve => setTimeout(resolve, 20));
          }
        })().catch(() => { fail('音频传输失败，请重试录音。'); }).finally(() => { pumping = undefined; });
        return pumping;
      };
      const release = () => {
        stream?.getTracks().forEach((track) => track.stop());
        source?.disconnect(); node?.disconnect();
        if (context && context.state !== 'closed') void context.close().catch(() => {});
      };
      const cleanup = () => { clearTimeout(flushTimer); queued.length = 0; queuedBytes = 0; finishResolve?.(); signal?.removeEventListener('abort', cancel); release(); };
      const cancel = () => { if (ended) return; ended = true; connection?.cancel(); controller.abort(); cleanup(); };
      const fail = (message: string, confirmed?: string) => { if (ended) return; cancel(); handlers.onError?.(message, confirmed); };
      const stop = () => {
        if (finishing || ended) return;
        finishing = true; handlers.onTranscribing?.();
        void (async () => {
          await new Promise<void>((resolve) => {
            finishResolve = resolve;
            flushTimer = setTimeout(() => fail('录音设备未能完成收尾，请重新录音。'), 2000);
            node!.port.postMessage('finish');
          });
          clearTimeout(flushTimer); release();
          await connected;
          await pump();
          if (!ended) connection!.finish();
        })();
      };
      signal?.addEventListener('abort', cancel, { once: true });
      try {
        if (signal?.aborted) throw new DOMException('录音已取消', 'AbortError');
        stream = await navigator.mediaDevices.getUserMedia({ audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true } });
        if (ended) { release(); throw new DOMException('录音已取消', 'AbortError'); }
        context = new AudioContext({ sampleRate: RECORDING_SAMPLE_RATE });
        if (context.sampleRate !== RECORDING_SAMPLE_RATE) throw new Error('当前设备不支持 16 kHz 录音，请更换麦克风。');
        await context.resume();
        const base = window.location.pathname.startsWith('/admin') ? '/admin/' : '/';
        await context.audioWorklet.addModule(`${base}audio/pcm-capture.js`);
        if (controller.signal.aborted) throw new DOMException('录音已取消', 'AbortError');
        source = context.createMediaStreamSource(stream);
        node = new AudioWorkletNode(context, 'pudding-pcm-capture');
        node.port.onmessage = ({ data }) => {
          if (ended) return;
          if (data === 'finished') { finishResolve?.(); return; }
          const chunk = data as Float32Array;
          const bounded = chunk.subarray(0, Math.max(0, MAX_RECORDING_SECONDS * RECORDING_SAMPLE_RATE - samples));
          if (!bounded.length) return;
          samples += bounded.length;
          const pcm = encodePcm16(bounded);
          queued.push(pcm); queuedBytes += pcm.byteLength;
          if (queuedBytes > RECORDING_SAMPLE_RATE * 2 * 15) { fail('语音连接过慢，请重试录音。'); return; }
          if (connection) void pump();
          let energy = 0;
          for (const sample of bounded) energy += sample * sample;
          handlers.onLevel?.(Math.min(1, Math.sqrt(energy / bounded.length) * 4), samples / RECORDING_SAMPLE_RATE);
          if (samples >= MAX_RECORDING_SECONDS * RECORDING_SAMPLE_RATE) stop();
        };
        source.connect(node); node.connect(context.destination);
        stream.getAudioTracks().forEach((track) => { track.onended = () => { if (!finishing && !ended) fail('麦克风已断开，请重新录音。'); }; });
        handlers.onPermissionGranted?.(stream.getAudioTracks()[0]?.label);
        connected = connectRealtimeAsr({
          onTranscript: (text, confirmed) => {
            if (ended) return;
            if (handlers.onTranscript) handlers.onTranscript(text, confirmed);
            else handlers.onInterimTranscript?.(text);
          },
          onCompleted: (text) => { if (ended) return; ended = true; cleanup(); handlers.onFinalTranscript?.(text); },
          onError: fail,
        }, controller.signal).then(async value => {
          if (ended) { value.cancel(); return; }
          connection = value; await pump();
        }).catch(error => { if (!ended) fail(error instanceof Error ? error.message : '实时语音连接失败。'); });
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
