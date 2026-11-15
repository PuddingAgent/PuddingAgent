import { requestAsrTicket } from './api';

export interface RealtimeAsrConnection {
  sendAudio: (pcm: ArrayBuffer) => void;
  finish: () => void;
  cancel: () => void;
}
export interface RealtimeAsrHandlers {
  onTranscript: (text: string, confirmedText: string) => void;
  onCompleted: (text: string) => void;
  onError: (message: string, confirmedText?: string) => void;
}

/** Local wire protocol; provider credentials/model selection remain in Core. */
export async function connectRealtimeAsr(handlers: RealtimeAsrHandlers, signal: AbortSignal): Promise<RealtimeAsrConnection> {
  const { ticket } = await requestAsrTicket(signal);
  if (signal.aborted) throw new DOMException('录音已取消', 'AbortError');
  const url = new URL('/api/voice/asr/stream', window.location.href);
  url.protocol = url.protocol === 'https:' ? 'wss:' : 'ws:';
  const socket = new WebSocket(url.href);
  return new Promise((resolve, reject) => {
    let ready = false;
    let ended = false;
    let finishing = false;
    let confirmed = '';
    let timer: ReturnType<typeof setTimeout>;
    const cleanup = () => { clearTimeout(timer); signal.removeEventListener('abort', abort); };
    const close = () => { if (socket.readyState === WebSocket.OPEN || socket.readyState === WebSocket.CONNECTING) socket.close(); };
    const fail = (message: string) => {
      if (ended) return;
      ended = true; cleanup(); close();
      if (!ready) reject(new Error(message)); else handlers.onError(message, confirmed);
    };
    const cancel = () => {
      if (ended) return;
      ended = true; cleanup();
      if (socket.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ type: 'cancel' }));
      close();
      if (!ready) reject(new DOMException('录音已取消', 'AbortError'));
    };
    const abort = () => cancel();
    const connection: RealtimeAsrConnection = {
      cancel,
      sendAudio: (pcm) => {
        if (ended || finishing) return;
        if (socket.readyState !== WebSocket.OPEN) { fail('实时语音连接中断，请重试。'); return; }
        if (socket.bufferedAmount > 128 * 1024) { fail('网络传输跟不上录音，请重新录音。'); return; }
        socket.send(pcm);
      },
      finish: () => {
        if (ended || finishing) return;
        finishing = true;
        if (socket.readyState !== WebSocket.OPEN) { fail('实时语音连接中断，请重试。'); return; }
        socket.send(JSON.stringify({ type: 'finish' }));
        timer = setTimeout(() => fail('最终转写等待超时，已保留确认文字，请重试。'), 20000);
      },
    };
    timer = setTimeout(() => fail('实时语音连接超时，请检查网络后重试。'), 15000);
    signal.addEventListener('abort', abort, { once: true });
    socket.onopen = () => {
      if (ended || signal.aborted) { cancel(); return; }
      socket.send(JSON.stringify({ type: 'start', ticket, format: 'pcm', sampleRate: 16000 }));
    };
    socket.onmessage = ({ data }) => {
      if (ended) return;
      try {
        const event = JSON.parse(String(data));
        if (event.type === 'ready' && !ready) { clearTimeout(timer); ready = true; resolve(connection); }
        else if (event.type === 'transcript' && ready) {
          if (typeof event.transcript !== 'string' || typeof event.confirmedText !== 'string') throw new Error('invalid transcript');
          confirmed = event.confirmedText;
          handlers.onTranscript(event.transcript, confirmed);
        } else if (event.type === 'completed' && ready && finishing) {
          ended = true; cleanup(); close(); handlers.onCompleted(event.text || '');
        } else if (event.type === 'failed') {
          if (typeof event.confirmedText === 'string') confirmed = event.confirmedText;
          fail(event.message || '实时语音识别失败，请重试。');
        } else throw new Error('invalid event');
      } catch { fail('实时语音响应无效，请重新录音。'); }
    };
    socket.onerror = () => fail('无法连接实时语音服务，请检查网络后重试。');
    socket.onclose = () => { if (!ended) fail('实时语音连接提前中断，已保留确认文字。'); };
    if (signal.aborted) cancel();
  });
}
