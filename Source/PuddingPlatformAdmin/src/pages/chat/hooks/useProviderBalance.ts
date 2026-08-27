import { useCallback, useEffect, useRef, useState } from 'react';
import {
  getLlmProviderBalance,
  type LlmProviderBalanceDto,
} from '@/services/platform/api';
import { usePollingLoader } from './usePollingLoader';

/** 余额低频轮询间隔：任务约定手动或 >5min，避免高频请求上游 balance API。 */
const BALANCE_REFRESH_INTERVAL_MS = 5 * 60 * 1000;

export interface ProviderBalanceState {
  /** 主币种总额；查询失败/不可用时为 undefined（徽标显示 '—'）。 */
  balance?: number;
  /** ISO 货币代码（如 'CNY'），由展示层映射为符号。 */
  currency?: string;
  /** 赠送额（主币种第一项），供 Tooltip 明细；缺失为 undefined。 */
  grantedBalance?: number;
  /** 充值额（主币种第一项），供 Tooltip 明细；缺失为 undefined。 */
  toppedUpBalance?: number;
  /** 上游查询时间（ISO 字符串，仅成功查询时返回），供 Tooltip 明细。 */
  queriedAt?: string;
  /** 查询失败原因（上游 error 或本地异常），进 Tooltip 第二行。 */
  errorText?: string;
  /** 是否有余额请求在途（手动刷新/轮询共用），供徽标 loading 反馈。 */
  loading: boolean;
  /** 手动刷新（徽标点击触发）。 */
  refresh: () => void;
}

/**
 * 当前主代理所用 LLM 服务商的账户余额。
 * providerId 变化立即拉取一次，此后每 5 分钟低频轮询（页面隐藏自动暂停）；
 * 任何失败都静默降级为 balance=undefined + errorText，绝不向上抛错。
 */
export function useProviderBalance(
  providerId: string | undefined,
  enabled: boolean,
): ProviderBalanceState {
  const [balance, setBalance] = useState<number | undefined>(undefined);
  const [currency, setCurrency] = useState<string | undefined>(undefined);
  const [grantedBalance, setGrantedBalance] = useState<number | undefined>(
    undefined,
  );
  const [toppedUpBalance, setToppedUpBalance] = useState<number | undefined>(
    undefined,
  );
  const [queriedAt, setQueriedAt] = useState<string | undefined>(undefined);
  const [errorText, setErrorText] = useState<string | undefined>(undefined);
  const [loading, setLoading] = useState(false);

  // 请求序号：providerId 快速切换时丢弃过期响应
  const seqRef = useRef(0);

  const load = useCallback(() => {
    if (!providerId || !enabled) return;
    const seq = (seqRef.current += 1);
    setLoading(true);
    getLlmProviderBalance(providerId)
      .then((dto: LlmProviderBalanceDto) => {
        if (seq !== seqRef.current) return;
        const first = dto.isAvailable ? dto.balanceInfos?.[0] : undefined;
        setBalance(first?.totalBalance);
        setCurrency(first?.currency);
        setGrantedBalance(first?.grantedBalance);
        setToppedUpBalance(first?.toppedUpBalance);
        setQueriedAt(first ? dto.queriedAt : undefined);
        setErrorText(first ? undefined : (dto.error ?? '余额不可用'));
        setLoading(false);
      })
      .catch((err: unknown) => {
        if (seq !== seqRef.current) return;
        setBalance(undefined);
        setCurrency(undefined);
        setGrantedBalance(undefined);
        setToppedUpBalance(undefined);
        setQueriedAt(undefined);
        setErrorText(err instanceof Error ? err.message : String(err));
        setLoading(false);
      });
  }, [providerId, enabled]);

  // providerId/开关变化：重置状态并立即拉取一次
  useEffect(() => {
    setBalance(undefined);
    setCurrency(undefined);
    setGrantedBalance(undefined);
    setToppedUpBalance(undefined);
    setQueriedAt(undefined);
    setErrorText(undefined);
    if (!providerId || !enabled) {
      setLoading(false);
      return;
    }
    load();
  }, [providerId, enabled, load]);

  // 低频轮询 + 页面隐藏自动暂停；refresh 供徽标点击手动刷新
  const { refresh } = usePollingLoader(
    load,
    enabled && !!providerId,
    BALANCE_REFRESH_INTERVAL_MS,
    [providerId],
  );

  return {
    balance,
    currency,
    grantedBalance,
    toppedUpBalance,
    queriedAt,
    errorText,
    loading,
    refresh,
  };
}
