// ── useSubAgentPool：子代理池取数 hook ───────────────────────────
// 三态由 hook 显式区分，UI 不得自行推断：
//   loading=true                  → 加载中（data 为 null）
//   error !== null                → 加载失败（data 为 null）
//   loading=false && error=null   → 请求成功：data=[] 即空池，data=[...] 即有数据
// 「空池」是成功态的一种，不是错误，也不是加载中。
import { useCallback, useEffect, useRef, useState } from 'react';
import { fetchSubAgentPool, fetchSubAgentPoolCapacity, type SubAgentPoolEntry } from './api';
import { describeSubAgentPoolError } from './errors';

export interface UseSubAgentPoolResult {
  /** 请求成功后的结果；未成功（加载中 / 失败）时为 null。 */
  data: SubAgentPoolEntry[] | null;
  loading: boolean;
  /** 失败文案；成功时为 null。 */
  error: string | null;
  /**
   * 池容量上限；未拿到时为 null。
   *
   * **刻意不归入三态**：容量是纯展示用的辅助信息，它的请求失败只让这一栏
   * 不显示，**不得**把 `error` 置位或让 `data` 变 null。一个数字没有资格
   * 把一个能正常显示的列表降级成错误页。
   */
  capacity: number | null;
  reload: () => void;
}

export function useSubAgentPool(): UseSubAgentPoolResult {
  const [data, setData] = useState<SubAgentPoolEntry[] | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [capacity, setCapacity] = useState<number | null>(null);
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

    // 容量与列表并行取，但生命周期完全独立：
    // 列表失败才是失败；容量失败只是「这一栏没数字」。
    fetchSubAgentPoolCapacity()
      .then((value) => {
        if (!alive || requestSeq.current !== seq) return;
        setCapacity(value);
      })
      .catch(() => {
        // 不设 error：容量拿不到不该把已成功取到的列表变成错误态。
        // 置 null 而不是保留上一次的值：配置可能已被改小，旧数字会变成谎报。
        if (!alive || requestSeq.current !== seq) return;
        setCapacity(null);
      });

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

  return { data, loading, error, capacity, reload };
}
