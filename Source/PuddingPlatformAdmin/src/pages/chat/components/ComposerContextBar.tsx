// ── ComposerContextBar：Composer 内嵌上下文窗口指示条 ─────────
// P0#3：微型进度条 + 百分比文本 + Compaction 状态；点击弹出 Popover，
// popup 内容复用 ContextMemoryIndicator 的上下文窗口明细（总占用/模型上下文/已使用）。
import { Popover } from 'antd';
import React, { useMemo } from 'react';
import { useChatStyles } from '../styles';

interface ComposerContextBarProps {
  tLimit: number;
  tUsed: number;
  tPct: number;
  cacheHitTokens?: number;
  cacheMissTokens?: number;
  cacheHitRate?: number;
  /** 来自 useCompaction hook 的压缩状态文案（如 "上次压缩: 2分钟前"） */
  compactionStatus?: string | null;
}

const clampPct = (value: number): number =>
  Number.isFinite(value) ? Math.max(0, Math.min(100, value)) : 0;

/** 与 ContextMemoryIndicator 保持一致的占用色阶 */
const getContextColors = (pct: number): { start: string; end: string } => {
  if (pct < 30) return { start: '#3b82f6', end: '#06b6d4' };
  if (pct < 60) return { start: '#06b6d4', end: '#8b5cf6' };
  if (pct < 85) return { start: '#8b5cf6', end: '#f97316' };
  return { start: '#f97316', end: '#ef4444' };
};

const formatTokens = (value: number): string => `${(value / 1024).toFixed(1)}K`;

const ComposerContextBar: React.FC<ComposerContextBarProps> = ({
  tLimit,
  tUsed,
  tPct,
  cacheHitTokens,
  cacheMissTokens,
  cacheHitRate,
  compactionStatus,
}) => {
  const { styles } = useChatStyles();
  const pct = clampPct(tPct);
  const colors = getContextColors(pct);
  const configured = tLimit > 0;
  const cacheTotal = (cacheHitTokens ?? 0) + (cacheMissTokens ?? 0);
  const effectiveHitRate =
    cacheHitRate ??
    (cacheTotal > 0
      ? Math.round(((cacheHitTokens ?? 0) / cacheTotal) * 100)
      : undefined);

  const popupContent = useMemo(
    () => (
      <div
        className={styles.composerContextBarPopup}
        data-testid="composer-context-bar-popup"
      >
        <div className={styles.composerContextBarPopupHeader}>
          <div className={styles.composerContextBarPopupTitle}>上下文窗口</div>
          <div className={styles.composerContextBarPopupStatus}>
            <span
              className={styles.composerContextBarPopupDot}
              style={{ background: colors.end }}
            />
            {pct.toFixed(1)}%
          </div>
        </div>
        <div className={styles.composerContextBarPopupBody}>
          {!configured ? (
            <div className={styles.composerContextBarPopupEmpty}>
              发送第一条消息后显示上下文窗口占用
            </div>
          ) : (
            <>
              <div className={styles.composerContextBarPopupRow}>
                <span className={styles.composerContextBarPopupLabel}>
                  总占用
                </span>
                <span className={styles.composerContextBarPopupValue}>
                  {formatTokens(tUsed)} / {formatTokens(tLimit)}
                </span>
              </div>
              <div className={styles.composerContextBarPopupProgress}>
                <span
                  className={styles.composerContextBarPopupProgressBar}
                  style={{
                    width: `${pct}%`,
                    background: `linear-gradient(90deg, ${colors.start}, ${colors.end})`,
                  }}
                />
              </div>
              <div className={styles.composerContextBarPopupRow}>
                <span className={styles.composerContextBarPopupLabel}>
                  模型上下文
                </span>
                <span className={styles.composerContextBarPopupValue}>
                  {formatTokens(tLimit)}
                </span>
              </div>
              <div className={styles.composerContextBarPopupRow}>
                <span className={styles.composerContextBarPopupLabel}>
                  已使用
                </span>
                <span className={styles.composerContextBarPopupValue}>
                  {tUsed > 0 ? formatTokens(tUsed) : '0'}
                </span>
              </div>
              {effectiveHitRate !== undefined && (
                <div className={styles.composerContextBarPopupRow}>
                  <span className={styles.composerContextBarPopupLabel}>
                    缓存命中率
                  </span>
                  <span className={styles.composerContextBarPopupValue}>
                    {effectiveHitRate}%
                  </span>
                </div>
              )}
              {compactionStatus && (
                <div className={styles.composerContextBarPopupRow}>
                  <span className={styles.composerContextBarPopupLabel}>
                    压缩
                  </span>
                  <span className={styles.composerContextBarPopupValue}>
                    {compactionStatus}
                  </span>
                </div>
              )}
              {pct > 80 && (
                <div className={styles.composerContextBarPopupWarning}>
                  ⚠ 上下文接近饱和，建议压缩对话
                </div>
              )}
            </>
          )}
        </div>
      </div>
    ),
    [
      colors.end,
      colors.start,
      compactionStatus,
      configured,
      effectiveHitRate,
      pct,
      styles,
      tLimit,
      tUsed,
    ],
  );

  const statusText = configured
    ? `上下文窗口 ${pct.toFixed(0)}% 已用`
    : '上下文窗口 未配置';

  return (
    <Popover
      content={popupContent}
      placement="topLeft"
      trigger="click"
      arrow={false}
    >
      <button
        type="button"
        className={styles.composerContextBar}
        data-testid="composer-context-bar"
        aria-label="查看上下文窗口占用"
      >
        <span className={styles.composerContextBarProgress}>
          <span
            className={styles.composerContextBarProgressFill}
            style={{
              width: configured ? `${pct}%` : '0%',
              background: `linear-gradient(90deg, ${colors.start}, ${colors.end})`,
            }}
          />
        </span>
        <span className={styles.composerContextBarText}>{statusText}</span>
        {compactionStatus && (
          <span
            className={styles.composerContextBarCompaction}
            title={compactionStatus}
          >
            {compactionStatus}
          </span>
        )}
      </button>
    </Popover>
  );
};

export default React.memo(ComposerContextBar);
