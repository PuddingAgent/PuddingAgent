// ── useSubAgentRuns：条目运行历史取数 hook ─────────────────────
// 与 useSubAgentPool 同一套三态纪律，另加一档「没有选中条目」：
//   name === null                → 不取数（runs=null / loading=false / error=null）
//   loading === true             → 加载中（runs 为 null）
//   error !== null               → 加载失败（runs 为 null）
//   loading=false && error=null  → 请求成功：runs=[] 即「从未执行」，runs=[...] 即有履历
//
// 「从未执行」是成功态的一种。把它渲染成错误或加载中，都会让人去查一个不存在的问题。
import { useCallback, useEffect, useRef, useState } from 'react';
import { fetchSubAgentRuns, type SubAgentRun, type SubAgentRunsPage } from './api';
import { describeSubAgentPoolError } from './errors';

const RUNS_ERROR_FALLBACK = '加载运行历史失败';

export interface UseSubAgentRunsResult {
  /** 请求成功后的本次返回条目；未成功（未选中 / 加载中 / 失败）时为 null。 */
  runs: SubAgentRun[] | null;
  /** 全量记录条数；未成功时为 0。仅用于判断截断，不要当成「已加载条数」。 */
  total: number;
  /** 是否因 limit 截断；未成功时为 false —— 没拿到结果时不能声称「完整」。 */
  truncated: boolean;
  loading: boolean;
  /** 失败文案；成功或未选中时为 null。 */
  error: string | null;
  reload: () => void;
}

export function useSubAgentRuns(name: string | null): UseSubAgentRunsResult {
  const [page, setPage] = useState<SubAgentRunsPage | null>(null);
  const [loading, setLoading] = useState(name !== null);
  const [error, setError] = useState<string | null>(null);
  const [reloadToken, setReloadToken] = useState(0);

  // 只认最后一次请求的结果：切换条目时，上一条的慢响应不得覆盖新条目的历史。
  const requestSeq = useRef(0);

  useEffect(() => {
    if (name === null) {
      // 清空上一条目的残留：否则切回空态时会闪出别人的履历。
      requestSeq.current += 1;
      setPage(null);
      setError(null);
      setLoading(false);
      return;
    }

    const seq = requestSeq.current + 1;
    requestSeq.current = seq;
    let alive = true;

    setLoading(true);
    setError(null);
    setPage(null);

    fetchSubAgentRuns(name)
      .then((result) => {
        if (!alive || requestSeq.current !== seq) return;
        setPage(result);
        setLoading(false);
      })
      .catch((caught: unknown) => {
        if (!alive || requestSeq.current !== seq) return;
        setPage(null);
        setError(describeSubAgentPoolError(caught, RUNS_ERROR_FALLBACK));
        setLoading(false);
      });

    return () => {
      alive = false;
    };
  }, [name, reloadToken]);

  const reload = useCallback(() => {
    setReloadToken((token) => token + 1);
  }, []);

  return {
    runs: page?.items ?? null,
    total: page?.total ?? 0,
    truncated: page?.truncated ?? false,
    loading,
    error,
    reload,
  };
}
