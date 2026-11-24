// ── useSubAgentPool：子代理池取数 hook ───────────────────────────
// 三态由 hook 显式区分，UI 不得自行推断：
//   loading=true                  → 加载中（data 为 null）
//   error !== null                → 加载失败（data 为 null）
//   loading=false && error=null   → 请求成功：data=[] 即空池，data=[...] 即有数据
// 「空池」是成功态的一种，不是错误，也不是加载中。
import { useCallback, useEffect, useRef, useState } from 'react';
import { fetchSubAgentPool, type SubAgentPoolEntry } from './api';
import { describeSubAgentPoolError } from './errors';

export interface UseSubAgentPoolResult {
  /** 请求成功后的结果；未成功（加载中 / 失败）时为 null。 */
  data: SubAgentPoolEntry[] | null;
  loading: boolean;
  /** 失败文案；成功时为 null。 */
  error: string | null;
  reload: () => void;
}

export function useSubAgentPool(): UseSubAgentPoolResult {
  const [data, setData] = useState<SubAgentPoolEntry[] | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [reloadToken, setReloadToken] = useState(0);

  // 只认最后一次请求的结果：重试期间旧的慢响应不得覆盖新状态。
  const requestSeq = useRef(0);

  useEffect(() => {
    const seq = requestSeq.current + 1;
    requestSeq.current = seq;
    let alive = true;

    setLoading(true);
    setError(null);
    setData(null);

    fetchSubAgentPool()
      .then((entries) => {
        if (!alive || requestSeq.current !== seq) return;
        setData(entries);
        setLoading(false);
      })
      .catch((caught: unknown) => {
        if (!alive || requestSeq.current !== seq) return;
        setData(null);
        setError(describeSubAgentPoolError(caught));
        setLoading(false);
      });

    return () => {
      alive = false;
    };
  }, [reloadToken]);

  const reload = useCallback(() => {
    setReloadToken((token) => token + 1);
  }, []);

  return { data, loading, error, reload };
}
