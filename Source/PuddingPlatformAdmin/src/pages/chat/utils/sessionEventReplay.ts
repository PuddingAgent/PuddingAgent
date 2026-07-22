import {
  type AdminChatStreamEvent,
  normalizeConversationEventType,
} from '@/services/platform/api';
import { SessionNotFoundError } from '../hooks/sessionRuntimeCleanup';
import type { SessionEventPageResponse } from '../types/chatStateTypes';
import { buildSessionEventReplayUrl } from './chatStateUtils';

export type NormalizedSessionEvent = AdminChatStreamEvent & {
  sequenceNum?: number;
  recordedAt?: string;
};

/** Normalizes persisted event wrappers and live events into one shape. */
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
  const rawRecordedAt =
    object.recordedAt ??
    object.RecordedAt ??
    object.recordedAtUtc ??
    object.RecordedAtUtc;
  const recordedAt =
    typeof rawRecordedAt === 'string' && rawRecordedAt.trim()
      ? rawRecordedAt
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

  const rawDataJson = object.dataJson ?? object.DataJson;
  if (
    Object.keys(payload).length === 0 &&
    typeof rawDataJson === 'string' &&
    rawDataJson.trim()
  ) {
    try {
      payload = JSON.parse(rawDataJson) as Record<string, unknown>;
    } catch {
      payload = {};
    }
  }

  const rawData = object.data ?? object.Data;
  if (
    Object.keys(payload).length === 0 &&
    typeof rawData === 'string' &&
    rawData.trim()
  ) {
    try {
      payload = JSON.parse(rawData) as Record<string, unknown>;
    } catch {
      payload = {};
    }
  } else if (
    Object.keys(payload).length === 0 &&
    rawData &&
    typeof rawData === 'object'
  ) {
    payload = rawData as Record<string, unknown>;
  }

  const rawType = object.type ?? object.Type;
  const wrapperEventType = object.eventType ?? object.EventType;
  const canonicalType = String(
    (rawType === 'event' && wrapperEventType ? wrapperEventType : rawType) ??
      wrapperEventType ??
      payload.type ??
      '',
  ).trim();
  if (!canonicalType) return null;

  return {
    ...object,
    ...payload,
    type: normalizeConversationEventType(canonicalType),
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
