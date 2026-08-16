/**
 * workspace-tasks SSE Watch 客户端（TB-04 §5.3）
 *
 * 复用 orchestration 的 SSE 消费模板：parseSseChunk + Last-Event-ID 指数退避
 * 重连；HTTP 400/401/403/404/409 抛错不重连；sequence <= cursor 的事件丢弃（幂等）。
 *
 * REST 的 13 个函数在 src/services/platform/api.ts（TB-04 §5.1）。
 */
import type { TaskEventWatchEvent } from './types';

export interface ParsedTaskSseFrame {
  id?: string;
  event?: string;
  data: unknown;
}

/** 解析 SSE 分块，容忍跨 chunk 拆分与孤立坏帧（对齐 orchestration parseSseChunk） */
export function parseSseChunk(
  remainder: string,
  chunk: string,
): { frames: ParsedTaskSseFrame[]; remainder: string } {
  const normalized = `${remainder}${chunk}`.replace(/\r\n/g, '\n');
  const blocks = normalized.split('\n\n');
  const nextRemainder = blocks.pop() ?? '';
  const frames: ParsedTaskSseFrame[] = [];

  for (const block of blocks) {
    if (!block.trim() || block.startsWith(':')) continue;
    let id: string | undefined;
    let event: string | undefined;
    const dataLines: string[] = [];
    for (const line of block.split('\n')) {
      if (line.startsWith('id:')) id = line.slice(3).trimStart();
      else if (line.startsWith('event:')) event = line.slice(6).trimStart();
      else if (line.startsWith('data:'))
        dataLines.push(line.slice(5).trimStart());
    }
    if (dataLines.length === 0) continue;
    try {
      frames.push({ id, event, data: JSON.parse(dataLines.join('\n')) });
    } catch {
      // 孤立坏帧；后续已提交帧仍可消费。
    }
  }

  return { frames, remainder: nextRemainder };
}

const HTTP_NO_RECONNECT = new Set([400, 401, 403, 404, 409]);

class TaskWatchHttpError extends Error {
  constructor(
    message: string,
    readonly status: number,
  ) {
    super(message);
    this.name = 'TaskWatchHttpError';
  }
}

const waitForReconnect = (milliseconds: number, signal: AbortSignal) =>
  new Promise<void>((resolve) => {
    if (signal.aborted) {
      resolve();
      return;
    }
    const timer = window.setTimeout(() => {
      signal.removeEventListener('abort', handleAbort);
      resolve();
    }, milliseconds);
    const handleAbort = () => {
      window.clearTimeout(timer);
      resolve();
    };
    signal.addEventListener('abort', handleAbort, { once: true });
  });

export interface TaskWatchOptions {
  workspaceId: string;
  afterSequence: number;
  signal: AbortSignal;
  onEvent: (event: TaskEventWatchEvent) => void;
  onError?: (error: Error) => void;
}

/** 订阅任务事件流，断线按 Last-Event-ID 追赶；终态不丢不重。 */
export async function watchTasks(options: TaskWatchOptions): Promise<void> {
  let cursor = options.afterSequence;
  let reconnectAttempt = 0;

  while (!options.signal.aborted) {
    try {
      const token = localStorage.getItem('pudding_token');
      const headers: Record<string, string> = {
        Accept: 'text/event-stream',
      };
      if (token) headers.Authorization = `Bearer ${token}`;
      if (cursor > 0) headers['Last-Event-ID'] = String(cursor);
      const url = `/api/workspaces/${encodeURIComponent(
        options.workspaceId,
      )}/tasks/watch?afterSequence=${cursor}`;
      const response = await fetch(url, {
        method: 'GET',
        headers,
        signal: options.signal,
      });
      if (!response.ok || !response.body) {
        throw new TaskWatchHttpError(
          `任务事件流请求失败：HTTP ${response.status}`,
          response.status,
        );
      }

      const reader = response.body.getReader();
      const decoder = new TextDecoder();
      let remainder = '';
      let receivedEvent = false;
      while (!options.signal.aborted) {
        const { done, value } = await reader.read();
        const decoded = value
          ? decoder.decode(value, { stream: !done })
          : decoder.decode();
        const parsed = parseSseChunk(remainder, decoded);
        remainder = parsed.remainder;
        for (const frame of parsed.frames) {
          // 只消费携带 TaskEventWatchEvent 形状的帧；心跳/快照/未知帧自动跳过。
          // 事件名以 TB-04 §5.3 为准为 `task.event`，但为兼容 TB-03.1 并行后端的
          // `task.{eventType}` 约定，这里按 data 形状而非事件名校验。
          const event = frame.data as Partial<TaskEventWatchEvent>;
          if (
            !event.taskId ||
            !event.eventType ||
            !Number.isFinite(event.sequence)
          ) {
            continue;
          }
          if ((event.sequence as number) <= cursor) continue;
          cursor = event.sequence as number;
          receivedEvent = true;
          options.onEvent(event as TaskEventWatchEvent);
        }
        if (done) break;
      }
      reconnectAttempt = receivedEvent ? 0 : reconnectAttempt + 1;
    } catch (error) {
      if (options.signal.aborted) return;
      const normalized =
        error instanceof Error ? error : new Error(String(error));
      options.onError?.(normalized);
      if (
        normalized instanceof TaskWatchHttpError &&
        HTTP_NO_RECONNECT.has(normalized.status)
      ) {
        throw normalized;
      }
      reconnectAttempt += 1;
    }

    const delay = Math.min(
      4000,
      1000 * 2 ** Math.min(Math.max(reconnectAttempt - 1, 0), 2),
    );
    await waitForReconnect(delay, options.signal);
  }
}
