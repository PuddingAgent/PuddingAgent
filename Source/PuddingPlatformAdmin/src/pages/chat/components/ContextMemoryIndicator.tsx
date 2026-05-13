import React, { useState, useRef, useEffect, useMemo } from 'react';
import { Tooltip } from 'antd';
import { useChatStyles } from '../styles';

interface ContextMemoryIndicatorProps {
  tLimit: number;
  tUsed: number;
  tPct: number;
  status: 'idle' | 'thinking' | 'executing' | 'streaming';
  loaded: boolean; // 是否已收到首次 SSE usage 事件
}

const ContextMemoryIndicator: React.FC<ContextMemoryIndicatorProps> = ({
  tLimit, tUsed, tPct, status, loaded,
}) => {
  const { styles } = useChatStyles();
  const [showPopup, setShowPopup] = useState(false);
  const popupRef = useRef<HTMLDivElement>(null);
  const indicatorRef = useRef<HTMLDivElement>(null);

  // 点击外部关闭
  useEffect(() => {
    if (!showPopup) return;
    const handler = (e: MouseEvent) => {
      if (popupRef.current && !popupRef.current.contains(e.target as Node)
          && indicatorRef.current && !indicatorRef.current.contains(e.target as Node)) {
        setShowPopup(false);
      }
    };
    document.addEventListener('mousedown', handler);
    return () => document.removeEventListener('mousedown', handler);
  }, [showPopup]);

  // 颜色渐变：健康(蓝青) → 中压(青紫) → 高压(紫橙) → 极限(橙暗红)
  const getColor = () => {
    if (tPct < 30) return { start: '#3b82f6', end: '#06b6d4' }; // 蓝→青
    if (tPct < 60) return { start: '#06b6d4', end: '#8b5cf6' }; // 青→紫
    if (tPct < 85) return { start: '#8b5cf6', end: '#f97316' }; // 紫→橙
    return { start: '#f97316', end: '#ef4444' }; // 橙→暗红
  };

  const colors = getColor();
  const circumference = 2 * Math.PI * 12;
  const offset = circumference - (tPct / 100) * circumference;
  const uniqueId = useMemo(() => `ig_${Math.random().toString(36).slice(2, 8)}`, []);

  const statusLabel = {
    idle: '就绪',
    thinking: '思考中',
    executing: '执行中',
    streaming: '生成中',
  }[status];

  return (
    <div className={styles.indicatorWrapper}>
      {/* 指示器图标 */}
      <Tooltip title={
        loaded
          ? `上下文窗口 ${(tUsed / 1024).toFixed(0)}K / ${(tLimit / 1024).toFixed(0)}K · ${tPct.toFixed(0)}%`
          : '首次对话后才能获取上下文窗口大小'
      }>
      <div
        ref={indicatorRef}
        className={styles.indicatorRing}
        onClick={(e) => { e.stopPropagation(); setShowPopup(!showPopup); }}
      >
        <svg width="28" height="28" viewBox="0 0 28 28">
          <circle cx="14" cy="14" r="12" fill="none" stroke="var(--earth-brown)" strokeWidth="1.5" opacity={0.15} />
          <circle cx="14" cy="14" r="12" fill="none" stroke={`url(#ig_${uniqueId})`} strokeWidth="2"
            strokeDasharray={circumference} strokeDashoffset={offset}
            strokeLinecap="round" transform="rotate(-90 14 14)"
            style={{ transition: 'stroke-dashoffset 0.8s ease', filter: 'drop-shadow(0 0 4px rgba(139,92,246,0.3))' }} />
          <circle cx="14" cy="14" r="5" fill="currentColor"
            opacity={status === 'idle' ? 0.35 : 0.75} style={{ transition: 'opacity 0.3s' }}
            className={ status === 'thinking' ? styles.indicatorPulse : status === 'executing' ? styles.indicatorSpin : '' } />
          <defs>
            <linearGradient id={`ig_${uniqueId}`} x1="0%" y1="0%" x2="100%" y2="100%">
              <stop offset="0%" stopColor={colors.start} /><stop offset="100%" stopColor={colors.end} />
            </linearGradient>
          </defs>
        </svg>
      </div>
      </Tooltip>

      {/* 悬浮窗 — 定位在右侧，向上弹出 */}
      {showPopup && (
        <div ref={popupRef} className={styles.indicatorPopup}>
          <div className={styles.popupHeader}>
            <div className={styles.popupTitle}>上下文窗口</div>
            <div className={styles.popupStatus}>
              <span className={styles.popupDot} style={{ background: colors.end }} />
              {statusLabel} · {tPct.toFixed(1)}%
            </div>
          </div>
          <div className={styles.popupBody}>
            {!loaded ? (
              <div className={styles.popupRow} style={{ justifyContent: 'center', opacity: 0.6, padding: '8px 0' }}>
                发送第一条消息后显示上下文窗口占用
              </div>
            ) : (<>
              <div className={styles.popupRow}>
                <span>总占用</span>
                <span className={styles.popupValue}>
                  {tLimit > 0 ? `${(tUsed / 1024).toFixed(1)}K / ${(tLimit / 1024).toFixed(0)}K` : '模型未配置'}
                </span>
              </div>
              <div className={styles.popupProgress}>
                <div className={styles.popupProgressBar} style={{ width: `${tPct}%`, background: `linear-gradient(90deg, ${colors.start}, ${colors.end})` }} />
              </div>
              <div className={styles.popupRow}>
                <span>模型上下文</span>
                <span className={styles.popupValue}>{tLimit > 0 ? `${(tLimit / 1024).toFixed(0)}K` : '未知'}</span>
              </div>
              <div className={styles.popupRow}>
                <span>已使用</span>
                <span className={styles.popupValue}>{tUsed > 0 ? `${(tUsed / 1024).toFixed(1)}K` : '0'}</span>
              </div>
              {tPct > 80 && (
                <div className={styles.popupWarning}>
                  ⚠ 上下文接近饱和，建议压缩对话
                </div>
              )}
            </>)}
          </div>
        </div>
      )}
    </div>
  );
};

export default ContextMemoryIndicator;
