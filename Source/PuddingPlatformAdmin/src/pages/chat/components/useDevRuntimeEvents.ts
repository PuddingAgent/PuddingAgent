// ── useDevRuntimeEvents ────────────────────────────────────────
// ADR-054 Step 9: DevPanel rawEvents 采集逻辑从 ChatMain 迁出。
// 只在 devMode 开启时收集 timeline 差异事件，默认不影响聊天首屏。

import { useEffect, useRef, useState } from 'react';
import type { ChatTurn } from '../types';
import type { DevRawEvent } from './DevPanel';

type TurnSnapshot = {
  reasoningCount: number;
  stepCount: number;
  answerLen: number;
  status: string;
  usageTotal: number;
};

const MAX_DEV_EVENTS = 2000;

/**
 * 根据 turns 变化增量收集 DevPanel 诊断事件。
 * devMode 关闭时直接返回空数组，不分配 map 或遍历 turns。
 */
export function useDevRuntimeEvents(
  devMode: boolean,
  turns: ChatTurn[],
): DevRawEvent[] {
  const [rawEvents, setRawEvents] = useState<DevRawEvent[]>([]);
  const turnSnapshotRef = useRef<Map<string, TurnSnapshot>>(new Map());

  useEffect(() => {
    if (!devMode) {
      return;
    }

    const now = Date.now();
    const nextSnapshot = new Map<string, TurnSnapshot>();
    const appended: DevRawEvent[] = [];

    for (const turn of turns) {
      const prev = turnSnapshotRef.current.get(turn.turnId);
      const items = turn.assistant.timelineItems ?? [];
      const thinkingCount = items.filter((i) => i.type === 'thinking').length;
      const stepCount = items.filter((i) => i.type !== 'thinking').length;
      const current: TurnSnapshot = {
        reasoningCount: thinkingCount,
        stepCount,
        answerLen: turn.assistant.answerMarkdown.length,
        status: turn.assistant.status,
        usageTotal: turn.assistant.usage?.totalTokens ?? 0,
      };

      if (!prev) {
        if (thinkingCount > 0) {
          appended.push(
            ...items
              .filter((i) => i.type === 'thinking')
              .map((x) => ({
                id: `evt-${turn.turnId}-thinking-${x.id}`,
                timestamp: now,
                event: 'thinking',
                payload: x.text ?? '',
              })),
          );
        }
        if (stepCount > 0) {
          appended.push(
            ...items
              .filter((i) => i.type !== 'thinking')
              .map((x) => ({
                id: `evt-${turn.turnId}-step-${x.id}`,
                timestamp: x.timestamp || now,
                event: 'step',
                payload: `[${x.status}] ${x.message}`,
              })),
          );
        }
      } else {
        if (current.reasoningCount > prev.reasoningCount) {
          const newBlocks = items
            .filter((i) => i.type === 'thinking')
            .slice(prev.reasoningCount);
          appended.push(
            ...newBlocks.map((x) => ({
              id: `evt-${turn.turnId}-thinking-${x.id}`,
              timestamp: now,
              event: 'thinking',
              payload: x.text ?? '',
            })),
          );
        }

        if (current.stepCount > prev.stepCount) {
          const newCards = items
            .filter((i) => i.type !== 'thinking')
            .slice(prev.stepCount);
          appended.push(
            ...newCards.map((x) => ({
              id: `evt-${turn.turnId}-step-${x.id}`,
              timestamp: x.timestamp || now,
              event: 'step',
              payload: `[${x.status}] ${x.message}`,
            })),
          );
        }

        if (current.answerLen > prev.answerLen) {
          const delta = turn.assistant.answerMarkdown.slice(prev.answerLen);
          if (delta.trim()) {
            appended.push({
              id: `evt-${turn.turnId}-delta-${current.answerLen}`,
              timestamp: now,
              event: 'delta',
              payload: delta,
            });
          }
        }

        if (current.usageTotal > 0 && current.usageTotal !== prev.usageTotal) {
          appended.push({
            id: `evt-${turn.turnId}-usage-${current.usageTotal}`,
            timestamp: now,
            event: 'usage',
            payload: `totalTokens=${current.usageTotal}`,
          });
        }

        if (current.status !== prev.status) {
          const ev =
            current.status === 'success'
              ? 'done'
              : current.status === 'error'
                ? 'error'
                : current.status === 'cancelled'
                  ? 'cancelled'
                  : 'status';
          appended.push({
            id: `evt-${turn.turnId}-status-${current.status}-${now}`,
            timestamp: now,
            event: ev,
            payload: current.status,
          });
        }
      }

      nextSnapshot.set(turn.turnId, current);
    }

    turnSnapshotRef.current = nextSnapshot;
    if (appended.length > 0) {
      setRawEvents((prev) => [...prev, ...appended].slice(-MAX_DEV_EVENTS));
    }
  }, [devMode, turns]);

  return rawEvents;
}
