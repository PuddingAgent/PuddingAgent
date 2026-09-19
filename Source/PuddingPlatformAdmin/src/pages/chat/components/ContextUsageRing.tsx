// ── ContextUsageRing：上下文用量环形指示器（唯一的 composer 状态入口）────
// 用户诉求（2026-09-19）：
//  1. 悬浮显示基本信息、点击展开明细面板；
//  2. 左侧旧胶囊（ComposerFeedbackStrip 轻反馈带 + 状态胶囊）与圆环职责重复，
//     整条移除，其面板（ComposerStatusDetails 运行摘要）并入本弹层；
//  3. 尺寸与工具栏其它按钮（composerToolbarButton 34×34）对齐。
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
  /** context-health 拉取失败原因；有值时圆环区分「未配置」与「获取失败」。 */
  error?: string | null;
  /** 运行中的子代理数（原轻反馈带「子代理 N」胶囊的信息）。 */
  subAgentsRunning?: number;
  /** 打开子代理管理器（原轻反馈带的 onClick 入口，避免移除胶囊后丢失入口）。 */
  onOpenSubAgents?: () => void;
  /** 运行状态详情（原 ComposerStatusDetails 弹层内容，并入本面板）。 */
  runtimeDetails?: React.ReactNode;
}

const SIZE = 34;
const RING = 22;
const STROKE = 2.5;
const RADIUS = (RING - STROKE) / 2;
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
  error,
  subAgentsRunning,
  onOpenSubAgents,
  runtimeDetails,
}) => {
  const { styles } = useChatStyles();
  const [open, setOpen] = useState(false);
  const configured = tLimit > 0;
  const pct = clampPct(tPct);
  const color = contextUsageColor(pct);
  const dash = configured ? (pct / 100) * CIRCUMFERENCE : 0;

  const hoverSummary = configured
    ? `${pct.toFixed(1)}% · ${formatTokens(tUsed)} / ${formatTokens(tLimit)} 上下文已使用`
    : error
      ? `上下文用量获取失败：${error}`
      : '上下文窗口未配置';

  const handleOpenSubAgents = useCallback(() => {
    setOpen(false);
    onOpenSubAgents?.();
  }, [onOpenSubAgents]);

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
            {error ?? '发送第一条消息后显示上下文用量'}
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

        {/* 原轻反馈带「子代理 N」入口：胶囊移除后在此保留可达性。 */}
        {onOpenSubAgents && (
          <button
            type="button"
            className={styles.contextUsagePanelRowButton}
            data-testid="context-usage-subagents"
            onClick={handleOpenSubAgents}
          >
            <span className={styles.contextUsagePanelLabel}>子代理</span>
            <span className={styles.contextUsagePanelValue}>
              {(subAgentsRunning ?? 0) > 0
                ? `${subAgentsRunning} 个运行中`
                : '无运行中'}
              　打开管理器 →
            </span>
          </button>
        )}

        {/* 原 ComposerStatusDetails 弹层内容（运行摘要），整体并入。 */}
        {runtimeDetails && (
          <div
            className={styles.contextUsagePanelRuntime}
            data-testid="context-usage-runtime"
          >
            {runtimeDetails}
          </div>
        )}
      </div>
    ),
    [
      cacheHitRate,
      color,
      compactionStatus,
      configured,
      error,
      handleOpenSubAgents,
      onOpenSubAgents,
      pct,
      runtimeDetails,
      styles,
      subAgentsRunning,
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
          <svg
            width={RING}
            height={RING}
            viewBox={`0 0 ${RING} ${RING}`}
            aria-hidden="true"
          >
            <circle
              cx={RING / 2}
              cy={RING / 2}
              r={RADIUS}
              fill="none"
              stroke="color-mix(in srgb, var(--pudding-chat-text-muted) 22%, transparent)"
              strokeWidth={STROKE}
            />
            <circle
              cx={RING / 2}
              cy={RING / 2}
              r={RADIUS}
              fill="none"
              stroke={color}
              strokeWidth={STROKE}
              strokeLinecap="round"
              strokeDasharray={`${dash} ${CIRCUMFERENCE}`}
              transform={`rotate(-90 ${RING / 2} ${RING / 2})`}
            />
          </svg>
        </button>
      </Tooltip>
    </Popover>
  );
};

export default React.memo(ContextUsageRing);

export { SIZE as CONTEXT_USAGE_RING_SIZE };
