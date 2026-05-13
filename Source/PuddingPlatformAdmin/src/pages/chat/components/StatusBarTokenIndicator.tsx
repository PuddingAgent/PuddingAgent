// ── StatusBarTokenIndicator：状态栏 Token 用量指示器（微型圆环）────
import React, { useMemo } from 'react';
import { Tooltip } from 'antd';
import { useChatStyles } from '../styles';

interface StatusBarTokenIndicatorProps {
  tLimit: number;
  tUsed: number;
  tPct: number;
  status: 'idle' | 'streaming';
  loaded: boolean;
}

const StatusBarTokenIndicator: React.FC<StatusBarTokenIndicatorProps> = ({ tLimit, tUsed, tPct, status, loaded }) => {
  const { styles } = useChatStyles();
  const uniqueId = useMemo(() => `sbt_${Math.random().toString(36).slice(2, 6)}`, []);

  const getColor = () => {
    if (tPct < 30) return { start: '#3b82f6', end: '#06b6d4' };
    if (tPct < 60) return { start: '#06b6d4', end: '#8b5cf6' };
    if (tPct < 85) return { start: '#8b5cf6', end: '#f97316' };
    return { start: '#f97316', end: '#ef4444' };
  };
  const colors = getColor();
  const circumference = 2 * Math.PI * 6; // 半径 6，适配状态栏高度
  const offset = circumference - (tPct / 100) * circumference;

  if (!loaded) {
    return (
      <Tooltip title="上下文窗口 — 等待首次对话">
        <span className={styles.statusIcon}>
          <svg width="14" height="14" viewBox="0 0 14 14">
            <circle cx="7" cy="7" r="6" fill="none" stroke="var(--earth-brown)" strokeWidth="1" opacity={0.2} />
          </svg>
        </span>
      </Tooltip>
    );
  }

  return (
    <Tooltip title={`上下文 ${(tUsed / 1024).toFixed(0)}K / ${(tLimit / 1024).toFixed(0)}K · ${tPct.toFixed(0)}%`}>
      <span className={styles.statusIcon}>
        <svg width="14" height="14" viewBox="0 0 14 14">
          <circle cx="7" cy="7" r="6" fill="none" stroke="var(--earth-brown)" strokeWidth="1" opacity={0.15} />
          <circle cx="7" cy="7" r="6" fill="none" stroke={`url(#${uniqueId})`} strokeWidth="1.5"
            strokeDasharray={circumference} strokeDashoffset={offset}
            strokeLinecap="round" transform="rotate(-90 7 7)"
            style={{ transition: 'stroke-dashoffset 0.6s ease' }} />
          <circle cx="7" cy="7" r="2.5" fill="currentColor"
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
