// ── P1: SSE Transport Client ──────────────────────────────────
// 封装 subscribeSessionEvents，管理 cursor 推进和事件分发。
// ───────────────────────────────────────────────────────────────

import { subscribeSessionEvents } from '@/services/platform/api';
import type { AdminChatStreamEvent } from '@/services/platform/api';
import { recordPerfEvent } from '@/utils/debug';

export interface SseClientOptions {
  sessionId: string;
  afterSequence?: number;
  generation?: number;
  onEvent: (event: AdminChatStreamEvent, sequenceNum?: number) => void;
  onError?: (error: Error, httpStatus?: number) => void;
  signal?: AbortSignal;
}

export interface SseClientHandle {
  lastSequenceNum: number;
  abort: () => void;
}

export function createSseClient(options: SseClientOptions): SseClientHandle {
  const handle: SseClientHandle = {
    lastSequenceNum: options.afterSequence ?? 0,
    abort: () => {
      controller.abort();
    },
  };

  const controller = new AbortController();

  // Link external signal
  if (options.signal) {
    if (options.signal.aborted) {
      return handle;
    }
    options.signal.addEventListener('abort', () => controller.abort());
  }

  // P0: 使用向上转型的 onEvent 包装器，提取并记录 cursor
  const wrappedOnEvent = (event: AdminChatStreamEvent) => {
    const seq = (event as any).sequenceNum as number | undefined;
    if (seq !== undefined && seq > handle.lastSequenceNum) {
      handle.lastSequenceNum = seq;
    }
    options.onEvent(event, seq);
  };

  subscribeSessionEvents(options.sessionId, wrappedOnEvent, controller.signal, {
    onError: (error, httpStatus) => {
      recordPerfEvent('chat.sseClient.error', {
        sessionId: options.sessionId,
        generation: options.generation,
        error: error.message,
        httpStatus,
      });
      options.onError?.(error, httpStatus);
    },
    afterSequence: options.afterSequence,
    generation: options.generation,
  });

  return handle;
}
