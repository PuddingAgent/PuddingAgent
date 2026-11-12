// ── useGoalExecutionReview：PA-08 A 按 Goal 的只读评审投影 ────────
// 与 useExecutionReview 同一语义：单次读取、不轮询、不重试；过期响应丢弃，卸载 abort。
import { useCallback, useEffect, useRef, useState } from 'react';
import {
  getExecutionReviewByGoal,
  type ExecutionReviewSnapshotDto,
} from '@/services/platform/api';

export interface UseGoalExecutionReviewResult {
  snapshot: ExecutionReviewSnapshotDto | null;
  loading: boolean;
  error: string | null;
  refresh: () => Promise<void>;
}

export function useGoalExecutionReview(goalId?: string, options?: { stale?: boolean; enabled?: boolean }) {
  const enabled = options?.enabled ?? true;
  const stale = options?.stale ?? false;
  const [snapshot, setSnapshot] = useState<ExecutionReviewSnapshotDto | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const seq = useRef(0);
  const controller = useRef<AbortController | null>(null);

  const load = useCallback(async () => {
    if (!enabled || !goalId) {
      setSnapshot(null);
      setError(null);
      return;
    }
    const current = ++seq.current;
    controller.current?.abort();
    const next = new AbortController();
    controller.current = next;
    setLoading(true);
    try {
      const response = await getExecutionReviewByGoal(goalId, { stale, signal: next.signal });
      if (current !== seq.current) return;
      setSnapshot(response.review ?? null);
      setError(response.review ? null : 'execution_review_not_found');
    } catch (caught) {
      if (current !== seq.current || next.signal.aborted) return;
      setError(caught instanceof Error ? caught.message : 'review_unavailable');
    } finally {
      if (current === seq.current) setLoading(false);
    }
  }, [enabled, goalId, stale]);

  useEffect(() => {
    void load();
    return () => controller.current?.abort();
  }, [load]);

  return { snapshot, loading, error, refresh: load } satisfies UseGoalExecutionReviewResult;
}
