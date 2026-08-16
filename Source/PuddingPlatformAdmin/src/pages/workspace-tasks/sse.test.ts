import { TextDecoder, TextEncoder } from 'util';
import { ReadableStream } from 'stream/web';
import { parseSseChunk, watchTasks } from './api';
import type { TaskEventWatchEvent } from './types';

// jest-environment-jsdom 不暴露 Node 的 TextEncoder/TextDecoder/ReadableStream
Object.assign(globalThis, {
  TextEncoder,
  TextDecoder,
  ReadableStream,
});

describe('parseSseChunk（SSE 帧解析）', () => {
  it('解析跨 chunk 拆分的 task.event 帧并忽略心跳注释', () => {
    const first = parseSseChunk(
      '',
      ': ping\n\nid: 7\nevent: task.event\ndata: {"taskId":"t1","event',
    );
    expect(first.frames).toEqual([]);

    const second = parseSseChunk(
      first.remainder,
      'Type":"task.updated","sequence":7}\n\n',
    );
    expect(second.remainder).toBe('');
    expect(second.frames).toEqual([
      expect.objectContaining({
        id: '7',
        event: 'task.event',
        data: expect.objectContaining({ taskId: 't1', sequence: 7 }),
      }),
    ]);
  });

  it('孤立坏 JSON 帧不丢弃后续已提交帧', () => {
    const parsed = parseSseChunk(
      '',
      'event: task.event\ndata: {nope}\n\nid: 8\nevent: task.event\ndata: {"taskId":"t2","eventType":"task.created","sequence":8}\n\n',
    );
    expect(parsed.frames).toHaveLength(1);
    expect(parsed.frames[0]).toEqual(
      expect.objectContaining({ id: '8', event: 'task.event' }),
    );
  });
});

describe('watchTasks（SSE Cursor Watch）', () => {
  const originalFetch = globalThis.fetch;
  let fetchMock: jest.Mock;

  function sseResponse(chunks: string[]) {
    const encoder = new TextEncoder();
    let index = 0;
    const stream = new ReadableStream<Uint8Array>({
      pull(controller) {
        if (index < chunks.length) {
          controller.enqueue(encoder.encode(chunks[index]));
          index += 1;
        } else {
          controller.close();
        }
      },
    });
    return { ok: true, status: 200, body: stream } as unknown as Response;
  }

  const frame = (id: number, sequence: number): string =>
    `id: ${id}\nevent: task.event\ndata: ${JSON.stringify({
      taskId: 't1',
      workspaceId: 'default',
      sequence,
      eventType: 'task.updated',
      createdAtUtc: '2026-08-16T00:00:00Z',
    } satisfies TaskEventWatchEvent)}\n\n`;

  beforeEach(() => {
    fetchMock = jest.fn();
    globalThis.fetch = fetchMock as unknown as typeof fetch;
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
  });

  it('丢弃 sequence <= cursor 的迟到事件（幂等）', async () => {
    const received: TaskEventWatchEvent[] = [];
    const controller = new AbortController();
    fetchMock
      .mockResolvedValueOnce(sseResponse([frame(5, 5), frame(3, 3), frame(6, 6)]))
      .mockResolvedValueOnce(
        ({ ok: false, status: 500, body: null }) as unknown as Response,
      );

    await watchTasks({
      workspaceId: 'default',
      afterSequence: 0,
      signal: controller.signal,
      onEvent: (event) => {
        received.push(event);
        if (event.sequence >= 6) controller.abort();
      },
    });

    expect(received.map((e) => e.sequence)).toEqual([5, 6]);
  });

  it('携带 Last-Event-ID 与 afterSequence 游标请求', async () => {
    const controller = new AbortController();
    fetchMock
      .mockResolvedValueOnce(sseResponse([frame(9, 9)]))
      .mockResolvedValueOnce(
        ({ ok: false, status: 500, body: null }) as unknown as Response,
      );

    await watchTasks({
      workspaceId: 'default',
      afterSequence: 8,
      signal: controller.signal,
      onEvent: (event) => {
        if (event.sequence >= 9) controller.abort();
      },
    });

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/workspaces/default/tasks/watch?afterSequence=8',
      expect.objectContaining({
        headers: expect.objectContaining({ 'Last-Event-ID': '8' }),
      }),
    );
  });
});
