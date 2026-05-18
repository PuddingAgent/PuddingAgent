// ── StatusBarTokenIndicator：状态栏 Token 用量指示器（双层圆环）────
import React, { useMemo } from 'react';
import { Tooltip } from 'antd';
import { useChatStyles } from '../styles';

interface StatusBarTokenIndicatorProps {
  tLimit: number;
  tUsed: number;
  tPct: number;
  status: 'idle' | 'streaming';
  loaded: boolean;
  /** 会话累积缓存命中 tokens */
  cacheHitTokens?: number;
  /** 会话累积缓存未命中 tokens */
  cacheMissTokens?: number;
  /** 缓存命中率 0-100 */
  cacheHitRate?: number;
}

const StatusBarTokenIndicator: React.FC<StatusBarTokenIndicatorProps> = ({
  tLimit, tUsed, tPct, status, loaded,
  cacheHitTokens, cacheMissTokens, cacheHitRate,
}) => {
  const { styles } = useChatStyles();
  const uniqueId = useMemo(() => `sbt_${Math.random().toString(36).slice(2, 6)}`, []);
  const cacheGradId = useMemo(() => `sbtc_${Math.random().toString(36).slice(2, 6)}`, []);

  const getColor = () => {
    if (tPct < 30) return { start: '#3b82f6', end: '#06b6d4' };
    if (tPct < 60) return { start: '#06b6d4', end: '#8b5cf6' };
    if (tPct < 85) return { start: '#8b5cf6', end: '#f97316' };
    return { start: '#f97316', end: '#ef4444' };
  };

  const getCacheColor = () => {
    const rate = cacheHitRate ?? 0;
    if (rate > 80) return '#22c55e';
    if (rate > 50) return '#eab308';
    return '#ef4444';
  };

  const colors = getColor();
  const circumference = 2 * Math.PI * 6;
  const offset = circumference - (tPct / 100) * circumference;

  // 缓存环参数 (外环，半径更大)
  const cacheCircum = 2 * Math.PI * 6.5;
  const cacheOffset = cacheCircum - ((cacheHitRate ?? 0) / 100) * cacheCircum;
  const hasCache = (cacheHitTokens ?? 0) > 0 || (cacheMissTokens ?? 0) > 0;

  const formatK = (n: number) => (n / 1024).toFixed(0) + 'K';

  const tooltipTitle = loaded
    ? `上下文 ${formatK(tUsed)} / ${formatK(tLimit)} · ${tPct.toFixed(0)}%${hasCache ? `\n缓存命中 ${formatK(cacheHitTokens!)} / ${formatK((cacheHitTokens ?? 0) + (cacheMissTokens ?? 0))} · ${(cacheHitRate ?? 0).toFixed(0)}%` : ''}`
    : '上下文窗口 — 等待首次对话';

  if (!loaded) {
    return (
      <Tooltip title={tooltipTitle}>
        <span className={styles.statusIcon}>
          <svg width="14" height="14" viewBox="0 0 14 14">
            <circle cx="7" cy="7" r="6" fill="none" stroke="var(--earth-brown)" strokeWidth="1" opacity={0.2} />
          </svg>
        </span>
      </Tooltip>
    );
  }

  return (
    <Tooltip title={tooltipTitle}>
      <span className={styles.statusIcon}>
        <svg width="14" height="14" viewBox="0 0 14 14">
          {/* 背景轨 */}
          <circle cx="7" cy="7" r="6.5" fill="none" stroke="var(--earth-brown)" strokeWidth="0.6" opacity={0.1} />
          <circle cx="7" cy="7" r="5.5" fill="none" stroke="var(--earth-brown)" strokeWidth="0.8" opacity={0.12} />

          {/* 外环：缓存命中率 */}
          {hasCache && (
            <circle cx="7" cy="7" r="6.5" fill="none" stroke={getCacheColor()} strokeWidth="0.8"
              strokeDasharray={cacheCircum} strokeDashoffset={cacheOffset}
              strokeLinecap="round" transform="rotate(-90 7 7)"
              style={{ transition: 'stroke-dashoffset 0.6s ease' }} />
          )}

          {/* 内环：上下文窗口使用率 */}
          <circle cx="7" cy="7" r="5.5" fill="none" stroke={`url(#${uniqueId})`} strokeWidth="1.2"
            strokeDasharray={circumference} strokeDashoffset={offset}
            strokeLinecap="round" transform="rotate(-90 7 7)"
            style={{ transition: 'stroke-dashoffset 0.6s ease' }} />

          {/* 中心点 */}
          <circle cx="7" cy="7" r="2" fill="currentColor"
            opacity={status === 'idle' ? 0.35 : 0.75}
            className={status === 'streaming' ? styles.statusPulse : ''} />

          <defs>
            <linearGradient id={uniqueId} x1="0%" y1="0%" x2="100%" y2="100%">
              <stop offset="0%" stopColor={colors.start} /><stop offset="100%" stopColor={colors.end} />
            </linearGradient>
          </defs>
        </svg>
      </span>
    </Tooltip>
  );
};

export default StatusBarTokenIndicator;
