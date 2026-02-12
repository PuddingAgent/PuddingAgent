import { recognizeAsr } from '@/services/platform/api';
import type {
  BrowserVoiceInputAdapter,
  BrowserVoiceInputHandle,
  BrowserVoiceInputHandlers,
} from './browserVoiceInput';

/**
 * DashScope ASR 语音输入适配器。
 * 录音 → Blob → multipart/form-data → /api/voice/asr/recognize → 识别文本。
 * 实现 BrowserVoiceInputAdapter 接口，可直接替换浏览器原生 SpeechRecognition。
 * Phase 1 无流式中间结果（HTTP 非实时），stop 后一次性返回最终文本。
 */
export function createDashScopeVoiceInputAdapter(): BrowserVoiceInputAdapter {
  return {
    isSupported: () =>
      !!(
        navigator.mediaDevices?.getUserMedia &&
        typeof MediaRecorder !== 'undefined'
      ),

    async start(
      handlers: BrowserVoiceInputHandlers,
    ): Promise<BrowserVoiceInputHandle> {
      const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
      handlers.onPermissionGranted?.();

      let recorder: MediaRecorder | null = null;
      const chunks: Blob[] = [];
      let stopped = false;

      try {
        recorder = new MediaRecorder(stream, {
          mimeType: MediaRecorder.isTypeSupported('audio/webm')
            ? 'audio/webm'
            : undefined,
        });

        recorder.ondataavailable = (e) => {
          if (e.data.size > 0) chunks.push(e.data);
        };

        recorder.start();
      } catch {
        stream.getTracks().forEach((t) => t.stop());
        throw new Error('无法启动录音。');
      }

      return {
        stop: () => {
          if (stopped) return;
          stopped = true;

          if (recorder && recorder.state !== 'inactive') {
            recorder.onstop = async () => {
              stream.getTracks().forEach((t) => t.stop());
              if (chunks.length === 0) {
                handlers.onFinalTranscript?.('');
                return;
              }

              try {
                const blob = new Blob(chunks, {
                  type: recorder?.mimeType || 'audio/webm',
                });
                const result = await recognizeAsr(blob);
                handlers.onFinalTranscript?.(result.text);
              } catch (err) {
                handlers.onError?.(
                  err instanceof Error ? err.message : '语音识别失败',
                );
              }
            };
            recorder.stop();
          } else {
            stream.getTracks().forEach((t) => t.stop());
            handlers.onFinalTranscript?.('');
          }
        },
      };
    },
  };
}
