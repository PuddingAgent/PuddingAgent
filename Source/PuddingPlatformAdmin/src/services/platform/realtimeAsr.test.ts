import { requestAsrTicket } from './api';
import { connectRealtimeAsr } from './realtimeAsr';

jest.mock('./api', () => ({ requestAsrTicket: jest.fn() }));

class Socket {
  static OPEN = 1; static CONNECTING = 0; static instances: Socket[] = [];
  readyState = 0; bufferedAmount = 0;
  onopen?: () => void; onmessage?: (event: { data: string }) => void; onerror?: () => void; onclose?: () => void;
  send = jest.fn((_data: any) => {}); close = jest.fn(() => { this.readyState = 3; });
  constructor(public url: string) { Socket.instances.push(this); }
  open() { this.readyState = 1; this.onopen?.(); }
  event(value: object) { this.onmessage?.({ data: JSON.stringify(value) }); }
}

const originalSocket = globalThis.WebSocket;
beforeEach(() => {
  jest.useFakeTimers(); Socket.instances = [];
  (globalThis as any).WebSocket = Socket;
  (requestAsrTicket as jest.Mock).mockResolvedValue({ ticket: 'one-time-secret' });
});
afterEach(() => { (globalThis as any).WebSocket = originalSocket; jest.useRealTimers(); });
const handlers = () => ({ onTranscript: jest.fn(), onCompleted: jest.fn(), onError: jest.fn() });

async function open(callbacks = handlers()) {
  const controller = new AbortController();
  const pending = connectRealtimeAsr(callbacks, controller.signal); await Promise.resolve();
  const socket = Socket.instances[Socket.instances.length - 1]; socket.open(); socket.event({ type: 'ready' });
  return { socket, connection: await pending, callbacks, controller };
}

it('authenticates in the first frame and transcribes before finish', async () => {
  const { socket, connection, callbacks } = await open();
  expect(new URL(socket.url).search).toBe(''); expect(socket.url).not.toContain('secret');
  expect(JSON.parse(socket.send.mock.calls[0][0])).toEqual({ type: 'start', ticket: 'one-time-secret', format: 'pcm', sampleRate: 16000 });
  connection.sendAudio(new ArrayBuffer(3200));
  socket.event({ type: 'transcript', transcript: '临时', confirmedText: '' });
  expect(callbacks.onTranscript).toHaveBeenCalledWith('临时', '');
  expect(callbacks.onCompleted).not.toHaveBeenCalled();
  connection.finish(); connection.finish();
  expect(socket.send.mock.calls.filter(([data]) => typeof data === 'string' && data.includes('finish'))).toHaveLength(1);
  socket.event({ type: 'completed', text: '确认。' });
  expect(callbacks.onCompleted).toHaveBeenCalledWith('确认。'); expect(socket.close).toHaveBeenCalled();
});

it('cancel during final drain discards late transcripts', async () => {
  const { socket, connection, callbacks } = await open(); connection.finish(); connection.cancel();
  socket.event({ type: 'transcript', transcript: '迟到', confirmedText: '迟到' });
  socket.event({ type: 'completed', text: '迟到' });
  expect(callbacks.onTranscript).not.toHaveBeenCalled(); expect(callbacks.onCompleted).not.toHaveBeenCalled();
  expect(callbacks.onError).not.toHaveBeenCalled();
});

it('abort while connecting closes the socket and rejects startup', async () => {
  const controller = new AbortController(); const pending = connectRealtimeAsr(handlers(), controller.signal);
  await Promise.resolve(); controller.abort();
  await expect(pending).rejects.toThrow('录音已取消'); expect(Socket.instances[0].close).toHaveBeenCalled();
});

it('disconnect preserves only confirmed text and is never success', async () => {
  const { socket, callbacks } = await open();
  socket.event({ type: 'transcript', transcript: '确认。临时', confirmedText: '确认。' });
  socket.onclose?.(); expect(callbacks.onError).toHaveBeenCalledWith(expect.any(String), '确认。');
  expect(callbacks.onCompleted).not.toHaveBeenCalled();
});

it('bounds upload backpressure and times out final draining', async () => {
  const first = await open(); first.socket.bufferedAmount = 200000; first.connection.sendAudio(new ArrayBuffer(3200));
  expect(first.callbacks.onError).toHaveBeenCalledTimes(1);
  const second = await open(); second.connection.finish(); jest.advanceTimersByTime(20000);
  expect(second.callbacks.onError).toHaveBeenCalledTimes(1); expect(second.socket.close).toHaveBeenCalled();
});
