// ── TokenStatsIndicator：Token 消耗统计指示器 + 浮窗详细表格 ────
import React, { useCallback, useEffect, useRef, useState } from 'react';
import { useChatStyles } from '../styles';

interface TokenStatsRow {
  provider: string;
  inputTokens: number;
  cacheHit: number;
  outputTokens: number;
  totalTokens: number;
  requests: number;
  avgLatency: number;
  avgSpeed: number;
}

interface TokenRequestEntry {
  time: string;
  usage: number;
  inputTokens: number;
  cacheHit: number;
  outputTokens: number;
  latency: number;
  speed: number;
  provider: string;
}

interface TokenStatsIndicatorProps {
  stats?: TokenStatsRow[];
  recentRequests?: TokenRequestEntry[];
}

/** 格式化大数字为易读字符串 */
function fmtTokens(n: number): string {
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}M`;
  if (n >= 1_000) return `${(n / 1_000).toFixed(1)}K`;
  return String(n);
}

function fmtLatency(s: number): string {
  return `${s.toFixed(1)}s`;
}

function fmtSpeed(tps: number): string {
  return `${tps.toFixed(1)}t/s`;
}

const TokenStatsIndicator: React.FC<TokenStatsIndicatorProps> = ({ stats, recentRequests }) => {
  const { styles } = useChatStyles();
  const [visible, setVisible] = useState(false);
  const popupRef = useRef<HTMLDivElement>(null);
  const triggerRef = useRef<HTMLSpanElement>(null);

  useEffect(() => {
    if (!visible) return;
    const h = (e: MouseEvent) => {
      if (popupRef.current && !popupRef.current.contains(e.target as Node)
          && triggerRef.current && !triggerRef.current.contains(e.target as Node)) {
        setVisible(false);
      }
    };
    document.addEventListener('mousedown', h);
    return () => document.removeEventListener('mousedown', h);
  }, [visible]);

  const toggle = useCallback(() => setVisible(v => !v), []);

  // 汇总统计
  const totalInput = stats?.reduce((s, r) => s + r.inputTokens, 0) ?? 0;
  const totalOutput = stats?.reduce((s, r) => s + r.outputTokens, 0) ?? 0;

  return (
    <span className={styles.statusIconGroup} style={{ position: 'relative' }}>
      <span ref={triggerRef} className={styles.statusIconLabel} onClick={toggle} style={{ cursor: 'pointer' }}>
        {`今日: ${fmtTokens(totalInput + totalOutput)}`}
      </span>

      {visible && (
        <div ref={popupRef} className={styles.tokenStatsPopup}>
          <div className={styles.tokenStatsTitle}>GCMP: 今日 Token 消耗统计</div>

          {/* 汇总表 */}
          <table className={styles.tokenStatsTable}>
            <thead>
              <tr>
                <th>提供商</th><th>输入 Tokens</th><th>缓存命中</th><th>输出 Tokens</th>
                <th>消耗 Tokens</th><th>请求数</th><th>平均延迟</th><th>平均速度</th>
              </tr>
            </thead>
            <tbody>
              {stats?.map((r, i) => (
                <tr key={i}>
                  <td>{r.provider}</td>
                  <td>{fmtTokens(r.inputTokens)}</td>
                  <td>{fmtTokens(r.cacheHit)}</td>
                  <td>{fmtTokens(r.outputTokens)}</td>
                  <td>{fmtTokens(r.totalTokens)}</td>
                  <td>{r.requests}</td>
                  <td>{fmtLatency(r.avgLatency)}</td>
                  <td>{fmtSpeed(r.avgSpeed)}</td>
                </tr>
              )) ?? (
                <tr><td colSpan={8} style={{ textAlign: 'center', color: 'var(--earth-brown)', opacity: 0.5 }}>暂无数据</td></tr>
              )}
            </tbody>
          </table>

          {/* 详细请求列表 */}
          <div className={styles.tokenStatsTitle} style={{ marginTop: 10 }}>
            请求明细
          </div>
          <table className={styles.tokenStatsTable}>
            <thead>
              <tr>
                <th>请求时间</th><th>消耗量</th><th>输入 Tokens</th><th>缓存命中</th>
                <th>输出 Tokens</th><th>响应延迟</th><th>输出速度</th><th>提供商</th>
              </tr>
            </thead>
            <tbody>
              {recentRequests?.map((r, i) => (
                <tr key={i}>
                  <td>{r.time}</td>
                  <td>{fmtTokens(r.usage)}</td>
                  <td>{fmtTokens(r.inputTokens)}</td>
                  <td>{fmtTokens(r.cacheHit)}</td>
                  <td>{fmtTokens(r.outputTokens)}</td>
                  <td>{fmtLatency(r.latency)}</td>
                  <td>{fmtSpeed(r.speed)}</td>
                  <td>{r.provider}</td>
                </tr>
              )) ?? (
                <tr><td colSpan={8} style={{ textAlign: 'center', color: 'var(--earth-brown)', opacity: 0.5 }}>暂无数据</td></tr>
              )}
            </tbody>
          </table>
        </div>
      )}
    </span>
  );
};

export default TokenStatsIndicator;
