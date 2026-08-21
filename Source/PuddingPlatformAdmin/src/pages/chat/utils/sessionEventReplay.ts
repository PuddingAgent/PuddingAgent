import { type AdminChatStreamEvent } from '@/services/platform/api';
import { SessionNotFoundError } from '../hooks/sessionRuntimeCleanup';
import type { SessionEventPageResponse } from '../types/chatStateTypes';
import {
  buildSessionEventReplayUrl,
  getSessionEventSequenceNum,
} from './chatStateUtils';

export type NormalizedSessionEvent = AdminChatStreamEvent & {
  sequenceNum?: number;
  /** 时间锚点：canonical occurredAt（供子代理卡片等消费）。 */
  recordedAt?: string;
};

/** Returns the greatest durable sequence in a replay page. */
export function getMaxSessionEventSequenceNum(
  events: readonly unknown[],
): number | null {
  const maxSequence = events
    .map(getSessionEventSequenceNum)
    .filter((value): value is number => value !== null)
    .reduce(
      (maximum, value) => Math.max(maximum, value),
      Number.NEGATIVE_INFINITY,
    );
  return Number.isFinite(maxSequence) ? maxSequence : null;
}

/**
 * Normalizes canonical envelopes (SSE/replay/bootstrap 同形) into one shape.
 * TR-01/CU-02：canonical 事件名直通；无 canonical → legacy 映射，
 * 无旧 session_event_log wrapper（dataJson/Data/eventType）兼容分支。
 */
export function normalizeSessionEvent(
  raw: unknown,
): NormalizedSessionEvent | null {
  if (!raw || typeof raw !== 'object') return null;
  const object = raw as Record<string, unknown>;
  const rawSequence =
    object.sequence ??
    object.Sequence ??
    object.sequenceNum ??
    object.SequenceNum;
  const sequenceNum = rawSequence == null ? undefined : Number(rawSequence);
  const rawOccurredAt =
    object.occurredAt ??
    object.OccurredAt ??
    object.occurredAtUtc ??
    object.OccurredAtUtc;
  const recordedAt =
    typeof rawOccurredAt === 'string' && rawOccurredAt.trim()
      ? rawOccurredAt
      : undefined;

  let payload: Record<string, unknown> = {};
  const rawPayload = object.payload ?? object.Payload;
  if (rawPayload && typeof rawPayload === 'object') {
    payload = rawPayload as Record<string, unknown>;
  } else if (typeof rawPayload === 'string' && rawPayload.trim()) {
    try {
      payload = JSON.parse(rawPayload) as Record<string, unknown>;
    } catch {
      payload = {};
    }
  }

  const rawType = object.type ?? object.Type;
  const canonicalType = String(rawType ?? payload.type ?? '').trim();
  if (!canonicalType) return null;

  return {
    ...object,
    ...payload,
    type: canonicalType,
    ...(Number.isFinite(sequenceNum) ? { sequenceNum } : {}),
    ...(recordedAt ? { recordedAt } : {}),
  } as NormalizedSessionEvent;
}

export async function listSessionEventsPage(
  sessionId: string,
  from: number,
  limit: number,
  signal?: AbortSignal,
): Promise<SessionEventPageResponse> {
  const token = localStorage.getItem('pudding_token');
  const headers: Record<string, string> = {};
  if (token) headers.Authorization = `Bearer ${token}`;
  const url = buildSessionEventReplayUrl(sessionId, from, limit);
  const response = await fetch(url, { method: 'GET', headers, signal });
  if (!response.ok) {
    if (response.status === 404 || response.status === 410) {
      throw new SessionNotFoundError(
        sessionId,
        `replay HTTP ${response.status}`,
      );
    }
    throw new Error(`listSessionEvents failed: ${response.status}`);
  }
  return (await response.json()) as SessionEventPageResponse;
}
