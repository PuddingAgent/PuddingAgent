import { request } from '@umijs/max';
import type {
  OrchestrationCatalog,
  OrchestrationDraftValidateRequest,
  OrchestrationDraftValidationResult,
  OrchestrationEventPage,
  OrchestrationGraphCreateRequest,
  OrchestrationGraphDefinition,
  OrchestrationGraphDeleteReceipt,
  OrchestrationGraphLayout,
  OrchestrationGraphPage,
  OrchestrationHttpHookInvokeReceipt,
  OrchestrationHttpHookInvokeRequest,
  OrchestrationLayoutWriteRequest,
  OrchestrationManualRunReceipt,
  OrchestrationManualRunRequest,
  OrchestrationRevisionWriteRequest,
  OrchestrationRunEvent,
  OrchestrationRunPage,
  OrchestrationRunSnapshot,
  OrchestrationRunStatus,
} from './types';

export interface ParsedSseFrame {
  id?: string;
  event?: string;
  data: unknown;
}

export function encodeRevisionPath(revisionId: string): string {
  return revisionId.split('/').map(encodeURIComponent).join('/');
}

export function parseSseChunk(
  remainder: string,
  chunk: string,
): { frames: ParsedSseFrame[]; remainder: string } {
  const normalized = `${remainder}${chunk}`.replace(/\r\n/g, '\n');
  const blocks = normalized.split('\n\n');
  const nextRemainder = blocks.pop() ?? '';
  const frames: ParsedSseFrame[] = [];

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
      // A malformed frame is isolated to itself; the following committed frame remains consumable.
    }
  }

  return { frames, remainder: nextRemainder };
}

export async function getOrchestrationCatalog() {
  return request<OrchestrationCatalog>('/api/orchestrations/catalog');
}

export async function listOrchestrationGraphs(
  params: { workspaceId?: string; limit?: number; offset?: number } = {},
) {
  return request<OrchestrationGraphPage>('/api/orchestrations/graphs', {
    params,
  });
}

export async function createOrchestrationGraph(
  create: OrchestrationGraphCreateRequest,
) {
  return request<OrchestrationGraphDefinition>('/api/orchestrations/graphs', {
    method: 'POST',
    data: create,
  });
}

export async function deleteOrchestrationGraph(
  graphId: string,
  expectedCurrentRevision: number,
) {
  return request<OrchestrationGraphDeleteReceipt>(
    `/api/orchestrations/graphs/${encodeURIComponent(graphId)}`,
    {
      method: 'DELETE',
      params: { expectedCurrentRevision },
    },
  );
}

export async function listOrchestrationRuns(
  params: {
    workspaceId?: string;
    graphId?: string;
    status?: OrchestrationRunStatus;
    limit?: number;
    offset?: number;
  } = {},
) {
  return request<OrchestrationRunPage>('/api/orchestrations/runs', { params });
}

export async function getLatestOrchestrationRevision(graphId: string) {
  return request<OrchestrationGraphDefinition>(
    `/api/orchestrations/graphs/${encodeURIComponent(graphId)}/latest`,
  );
}

export async function getOrchestrationRun(runId: string) {
  return request<OrchestrationRunSnapshot>(
    `/api/orchestrations/runs/${encodeURIComponent(runId)}`,
  );
}

export async function startOrchestrationRun(
  command: OrchestrationManualRunRequest,
) {
  return request<OrchestrationManualRunReceipt>('/api/orchestrations/runs', {
    method: 'POST',
    data: command,
  });
}

export function getVisionArtifactUrl(
  workspaceId: string,
  artifactId: string,
): string {
  return `/api/workspaces/${encodeURIComponent(workspaceId)}/vision-artifacts/${encodeURIComponent(artifactId)}`;
}

export async function getOrchestrationRevision(revisionId: string) {
  return request<OrchestrationGraphDefinition>(
    `/api/orchestrations/revisions/${encodeRevisionPath(revisionId)}`,
  );
}

export async function getOrchestrationLayout(
  graphId: string,
  baseRevisionId: string,
): Promise<OrchestrationGraphLayout | undefined> {
  try {
    return await request<OrchestrationGraphLayout>(
      `/api/orchestrations/graphs/${encodeURIComponent(graphId)}/layout`,
      {
        params: { baseRevisionId },
        skipErrorHandler: true,
      },
    );
  } catch (error) {
    const status = (error as { response?: { status?: number } })?.response
      ?.status;
    if (status === 404) return undefined;
    throw error;
  }
}

export async function putOrchestrationLayout(
  graphId: string,
  write: OrchestrationLayoutWriteRequest,
) {
  return request<OrchestrationGraphLayout>(
    `/api/orchestrations/graphs/${encodeURIComponent(graphId)}/layout`,
    {
      method: 'PUT',
      data: write,
    },
  );
}

/**
 * Side-effect-free draft validation. The compiler runs against the draft exactly as it would
 * before a revision save; nothing is persisted (doc 84 §11.1).
 */
export async function validateOrchestrationDraft(
  graphId: string,
  requestBody: OrchestrationDraftValidateRequest,
) {
  return request<OrchestrationDraftValidationResult>(
    `/api/orchestrations/graphs/${encodeURIComponent(graphId)}/validate`,
    { method: 'POST', data: requestBody },
  );
}

/**
 * Appends the next immutable revision using head compare-and-swap. Returns 201 with the
 * server-authored revision, or 409 with current head facts on a stale expected revision.
 * Audit fields in the payload are preview information only (doc 83 §6 / ALREADY_KNOWN ③).
 */
export async function putOrchestrationRevision(
  graphId: string,
  write: OrchestrationRevisionWriteRequest,
) {
  return request<OrchestrationGraphDefinition>(
    `/api/orchestrations/graphs/${encodeURIComponent(graphId)}/revisions`,
    { method: 'PUT', data: write },
  );
}

export async function invokeOrchestrationHttpHook(
  graphId: string,
  revisionId: string,
  triggerId: string,
  body: OrchestrationHttpHookInvokeRequest,
) {
  return request<OrchestrationHttpHookInvokeReceipt>(
    `/api/orchestrations/hooks/${encodeURIComponent(graphId)}/${encodeURIComponent(triggerId)}`,
    {
      method: 'POST',
      params: { revisionId },
      data: body,
    },
  );
}

export async function getOrchestrationEvents(
  runId: string,
  afterSequence: number,
  limit = 500,
) {
  return request<OrchestrationEventPage>(
    `/api/orchestrations/runs/${encodeURIComponent(runId)}/events`,
    { params: { afterSequence, limit } },
  );
}

class OrchestrationWatchHttpError extends Error {
  constructor(
    message: string,
    readonly status: number,
  ) {
    super(message);
    this.name = 'OrchestrationWatchHttpError';
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

export async function watchOrchestrationRun(options: {
  runId: string;
  afterSequence: number;
  signal: AbortSignal;
  onEvent: (event: OrchestrationRunEvent) => void;
  onError?: (error: Error) => void;
}): Promise<void> {
  let cursor = options.afterSequence;
  let reconnectAttempt = 0;

  while (!options.signal.aborted) {
    try {
      const token = localStorage.getItem('pudding_token');
      const headers: Record<string, string> = { Accept: 'text/event-stream' };
      if (token) headers.Authorization = `Bearer ${token}`;
      if (cursor > 0) headers['Last-Event-ID'] = String(cursor);
      const url = `/api/orchestrations/runs/${encodeURIComponent(
        options.runId,
      )}/watch?afterSequence=${cursor}`;
      const response = await fetch(url, {
        method: 'GET',
        headers,
        signal: options.signal,
      });
      if (!response.ok || !response.body) {
        throw new OrchestrationWatchHttpError(
          `编排事件流请求失败：HTTP ${response.status}`,
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
          if (frame.event === 'orchestration.stream.error') {
            const payload = frame.data as { message?: string };
            throw new Error(
              payload.message || '编排事件流报告了持久化序列缺口',
            );
          }
          const event = frame.data as Partial<OrchestrationRunEvent>;
          if (
            !event.runId ||
            !event.eventType ||
            !Number.isFinite(event.sequence)
          )
            continue;
          if (frame.id && Number(frame.id) !== event.sequence) {
            throw new Error(
              `编排事件序列不一致：SSE ${frame.id} / payload ${event.sequence}`,
            );
          }
          if ((event.sequence as number) <= cursor) continue;
          cursor = event.sequence as number;
          receivedEvent = true;
          options.onEvent(event as OrchestrationRunEvent);
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
        normalized instanceof OrchestrationWatchHttpError &&
        [400, 401, 403, 404, 409].includes(normalized.status)
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
