// ── ContextUsageRing：上下文用量环形指示器 ────────────────────
// 用户诉求（2026-09-19）：移除输入框上方的旧上下文指示条（ComposerContextBar），
// 改为工具栏内的圆环控件 —— 悬浮显示基本信息，点击展开上下文明细面板。
// 数据源：ContextHealthSnapshot（usedTokens / contextWindowTokens / usageRatio）
// 经 IntentConsole 归一为 tLimit / tUsed / tPct。
import { Popover, Tooltip } from 'antd';
import React, { useCallback, useMemo, useState } from 'react';
import { useChatStyles } from '../styles';

export interface ContextUsageRingProps {
  /** 模型上下文窗口总量；0/缺失 = 未配置（不伪造数值）。 */
  tLimit: number;
  tUsed: number;
  /** 已使用百分比（0-100）。 */
  tPct: number;
  cacheHitRate?: number;
  /** 来自 useCompaction 的压缩状态文案（如「上次压缩：2分钟前」）。 */
  compactionStatus?: string | null;
}

const SIZE = 22;
const STROKE = 2.5;
const RADIUS = (SIZE - STROKE) / 2;
const CIRCUMFERENCE = 2 * Math.PI * RADIUS;

const clampPct = (value: number): number =>
  Number.isFinite(value) ? Math.max(0, Math.min(100, value)) : 0;

/** 占用色阶：与上下文反馈带同一套阈值语义（低=绿 / 中=橙 / 高=红）。 */
export const contextUsageColor = (pct: number): string =>
  pct >= 70 ? '#d84a3a' : pct >= 50 ? '#d98b28' : '#6f8f72';

const formatTokens = (value: number): string =>
  value >= 1024 ? `${(value / 1024).toFixed(1)}K` : String(Math.round(value));

const ContextUsageRing: React.FC<ContextUsageRingProps> = ({
  tLimit,
  tUsed,
  tPct,
  cacheHitRate,
  compactionStatus,
}) => {
  const { styles } = useChatStyles();
  const [open, setOpen] = useState(false);
  const configured = tLimit > 0;
  const pct = clampPct(tPct);
  const color = contextUsageColor(pct);
  const dash = configured ? (pct / 100) * CIRCUMFERENCE : 0;

  const hoverSummary = configured
    ? `${pct.toFixed(1)}% · ${formatTokens(tUsed)} / ${formatTokens(tLimit)} 上下文已使用`
    : '上下文窗口未配置';

  const panel = useMemo(
    () => (
      <div className={styles.contextUsagePanel} data-testid="context-usage-panel">
        <div className={styles.contextUsagePanelHeader}>
          <span className={styles.contextUsagePanelTitle}>上下文用量</span>
          <button
            type="button"
            className={styles.contextUsagePanelClose}
            aria-label="关闭上下文用量面板"
            onClick={() => setOpen(false)}
          >
            ✕
          </button>
        </div>
        {!configured ? (
          <div className={styles.contextUsagePanelEmpty}>
            发送第一条消息后显示上下文用量
          </div>
        ) : (
          <>
            <div className={styles.contextUsagePanelHeadline}>
              <span className={styles.contextUsagePanelPct}>
                {pct.toFixed(1)}%
              </span>
              <span className={styles.contextUsagePanelUsed}>
                已使用 {formatTokens(tUsed)}/{formatTokens(tLimit)}
              </span>
            </div>
            <div className={styles.contextUsagePanelProgress}>
              <span
                className={styles.contextUsagePanelProgressBar}
                style={{ width: `${pct}%`, background: color }}
              />
            </div>
            <div className={styles.contextUsagePanelBody}>
              <div className={styles.contextUsagePanelRow}>
                <span className={styles.contextUsagePanelLabel}>模型上下文</span>
                <span className={styles.contextUsagePanelValue}>
                  {formatTokens(tLimit)}
                </span>
              </div>
              <div className={styles.contextUsagePanelRow}>
                <span className={styles.contextUsagePanelLabel}>已使用</span>
                <span className={styles.contextUsagePanelValue}>
                  {formatTokens(tUsed)}
                </span>
              </div>
              {cacheHitRate !== undefined && (
                <div className={styles.contextUsagePanelRow}>
                  <span className={styles.contextUsagePanelLabel}>缓存命中率</span>
                  <span className={styles.contextUsagePanelValue}>
                    {Math.round(cacheHitRate)}%
                  </span>
                </div>
              )}
              {compactionStatus && (
                <div className={styles.contextUsagePanelRow}>
                  <span className={styles.contextUsagePanelLabel}>压缩</span>
                  <span className={styles.contextUsagePanelValue}>
                    {compactionStatus}
                  </span>
                </div>
              )}
            </div>
          </>
        )}
      </div>
    ),
    [
      cacheHitRate,
      color,
      compactionStatus,
      configured,
      pct,
      styles,
      tLimit,
      tUsed,
    ],
  );

  const handleOpenChange = useCallback((next: boolean) => setOpen(next), []);

  return (
    <Popover
      content={panel}
      placement="topRight"
      trigger="click"
      arrow={false}
      open={open}
      onOpenChange={handleOpenChange}
    >
      <Tooltip title={hoverSummary} placement="topRight">
        <button
          type="button"
          className={styles.contextUsageRing}
          data-testid="context-usage-ring"
          aria-label={hoverSummary}
        >
          <svg width={SIZE} height={SIZE} viewBox={`0 0 ${SIZE} ${SIZE}`} aria-hidden="true">
            <circle
              cx={SIZE / 2}
              cy={SIZE / 2}
              r={RADIUS}
              fill="none"
              stroke="color-mix(in srgb, var(--pudding-chat-text-muted) 22%, transparent)"
              strokeWidth={STROKE}
            />
            <circle
              cx={SIZE / 2}
              cy={SIZE / 2}
              r={RADIUS}
              fill="none"
              stroke={color}
              strokeWidth={STROKE}
              strokeLinecap="round"
              strokeDasharray={`${dash} ${CIRCUMFERENCE}`}
              transform={`rotate(-90 ${SIZE / 2} ${SIZE / 2})`}
            />
          </svg>
        </button>
      </Tooltip>
    </Popover>
  );
};

export default React.memo(ContextUsageRing);
