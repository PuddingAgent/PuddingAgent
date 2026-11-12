// ── useExecutionReview：PA-08 A 只读投影钩子 ─────────────────────
// 单次读取受权端点；不轮询、不重试、不写缓存为实时成功。过期响应按请求序号丢弃，
// 组件卸载/切换 review 时 abort。错误只暴露文本，不伪造快照。
import { useCallback, useEffect, useRef, useState } from 'react';
import {
  getExecutionReview,
  type ExecutionReviewSnapshotDto,
} from '@/services/platform/api';

export interface UseExecutionReviewResult {
  snapshot: ExecutionReviewSnapshotDto | null;
  loading: boolean;
  error: string | null;
  refresh: () => Promise<void>;
}

export function useExecutionReview(reviewId?: string, options?: { stale?: boolean; enabled?: boolean }) {
  const enabled = options?.enabled ?? true;
  const stale = options?.stale ?? false;
  const [snapshot, setSnapshot] = useState<ExecutionReviewSnapshotDto | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const seq = useRef(0);
  const controller = useRef<AbortController | null>(null);

  const load = useCallback(async () => {
    if (!enabled || !reviewId) {
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
      const response = await getExecutionReview(reviewId, { stale, signal: next.signal });
      if (current !== seq.current) return;
      setSnapshot(response.review ?? null);
      setError(response.review ? null : 'review_not_found');
    } catch (caught) {
      if (current !== seq.current || next.signal.aborted) return;
      setError(caught instanceof Error ? caught.message : 'review_unavailable');
    } finally {
      if (current === seq.current) setLoading(false);
    }
  }, [enabled, reviewId, stale]);

  useEffect(() => {
    void load();
    return () => controller.current?.abort();
  }, [load]);

  return { snapshot, loading, error, refresh: load } satisfies UseExecutionReviewResult;
}
