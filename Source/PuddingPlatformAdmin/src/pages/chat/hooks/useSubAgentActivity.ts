import { useCallback } from 'react';
import type { TimelineItem } from '../types';
import { sanitizeProcessText } from '../components/processPreview';

/**
 * Hook providing appendOrUpdateSubAgentActivity, extracted from useChatState.
 *
 * Merges or appends a TimelineItem for a specific sub-agent into an existing
 * timeline items list. When a matching spawned/progress item already exists
 * for the same subAgentId, its output is merged; otherwise the item is appended.
 *
 * @returns {{ appendOrUpdateSubAgentActivity }} — the activity merge function,
 *   stable across renders via useCallback.
 */
export function useSubAgentActivity() {
  const appendOrUpdateSubAgentActivity = useCallback(
    mergeSubAgentActivity,
    [],
  );

  return { appendOrUpdateSubAgentActivity };
}

export function mergeSubAgentActivity(
  subAgentId: string,
  items: TimelineItem[],
  next: TimelineItem,
  appendOutput?: string,
): TimelineItem[] {
  const idx = items.findIndex(
    (item) =>
      item.id === next.id ||
      (item.name === subAgentId &&
        (item.type === 'subagent_spawned' ||
          item.type === 'subagent_progress') &&
        next.type !== 'subagent_spawned'),
  );
  if (idx < 0) return [...items, next];
  const existing = items[idx];
  const isExactReplay = existing.id === next.id && appendOutput === undefined;
  const mergedOutput = sanitizeProcessText(
    isExactReplay
      ? (next.output ?? existing.output ?? '')
      : `${existing.output ?? ''}${appendOutput ?? next.output ?? ''}`,
    { compact: false },
  );
  const updated: TimelineItem = {
    ...existing,
    ...next,
    output:
      mergedOutput.length > 900
        ? mergedOutput.slice(mergedOutput.length - 900)
        : mergedOutput,
    arguments: next.arguments || existing.arguments,
    timestamp: next.timestamp,
  };
  return [...items.slice(0, idx), updated, ...items.slice(idx + 1)];
}
