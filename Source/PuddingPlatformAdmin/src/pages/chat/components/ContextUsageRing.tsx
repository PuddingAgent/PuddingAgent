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
import { COMPACTION_RUNNING_LABEL } from '../hooks/useCompaction';

export interface ContextUsageRingProps {
  /** 模型上下文窗口总量；0/缺失 = 未配置（不伪造数值）。 */
  tLimit: number;
  tUsed: number;
  /** 已使用百分比（0-100）。 */
  tPct: number;
  /** 注意：缓存命中率不在此展示 —— 同面板运行状态块已有一行，避免同值两处。 */
  /** 来自 useCompaction 的压缩状态文案（如「上次压缩：2分钟前」）。 */
  compactionStatus?: string | null;
  /** context-health 拉取失败原因；有值时圆环区分「未配置」与「获取失败」。 */
  error?: string | null;
  /** 运行中的子代理数（原轻反馈带「子代理 N」胶囊的信息）。 */
  subAgentsRunning?: number;
  /** 打开子代理管理器（原轻反馈带的 onClick 入口，避免移除胶囊后丢失入口）。 */
  onOpenSubAgents?: () => void;
  /** 有效输入窗口（模型窗口 − 预留输出）；用于把「系统预留」段画出来。 */
  tEffective?: number;
  /** 请求组装时的分层归因；缺失或全 0 时回落单色条。 */
  tBreakdown?: ContextUsageBreakdown | null;
  /** 用量数据来源/置信度：面板据此显示「数据来源」，让百分比可判断可信度。 */
  usageSource?: string | null;
  usageConfidence?: string | null;
  /** 运行状态详情（原 ComposerStatusDetails 弹层内容，并入本面板）。 */
  runtimeDetails?: React.ReactNode;
}

/** 上下文来源分层（与后端 ContextUsageSnapshot 同一口径，各桶互斥且穷尽）。 */
export interface ContextUsageBreakdown {
  systemPrompt: number;
  toolDefinitions: number;
  compactionSummary: number;
  conversation: number;
  reasoning: number;
  toolResults: number;
}

/** 色段顺序 = 图例顺序；色序对齐参考设计图（蓝/绿/紫/橙/青/玫瑰）。 */
const CONTEXT_SEGMENT_DEFS: ReadonlyArray<{
  key: keyof ContextUsageBreakdown;
  label: string;
  color: string;
}> = [
  { key: 'systemPrompt', label: '系统提示词', color: '#5b7fd4' },
  { key: 'toolDefinitions', label: '工具定义', color: '#6f8f72' },
  { key: 'compactionSummary', label: '压缩后记忆', color: '#8a6fd4' },
  { key: 'conversation', label: '对话消息', color: '#d98b28' },
  { key: 'reasoning', label: '思维链', color: '#3f9a9a' },
  { key: 'toolResults', label: '工具结果', color: '#c26b7a' },
];

/**
 * 系统预留（输出预留）：窗口里**不可用于输入**的部分。
 * 用户诉求（2026-09-19）：置于进度条**最右端**，用斜纹 /// + 灰色表示「不可使用」，
 * 从而让中间那段真正可用的空间一眼可辨 —— 此前它夹在「已使用」与「可用余量」之间，
 * 无法从条上读出哪一段能用。
 */
const CONTEXT_RESERVED_HATCH =
  'repeating-linear-gradient(45deg, rgba(185,169,155,0.95) 0 2px, rgba(185,169,155,0.28) 2px 5px)';
/** 可用余量标记：非填充，用虚线框表示「空的可填充区」，与预留的斜纹区分。 */
const CONTEXT_AVAILABLE_OUTLINE = '1px dashed rgba(140,122,106,0.65)';

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

/** 用量来源文案：Provider 报数可直接采信；本地估算/DB 回退可能明显偏高。 */
const usageProvenanceLabel = (
  confidence?: string | null,
  source?: string | null,
): string => {
  if (confidence === 'provider_reported') return 'Provider 报数';
  if (confidence === 'estimated') return '本地估算（可能偏高）';
  return source ?? '未知';
};

// 与 ComposerStatusDetails / 上下文胶囊同一口径（÷1000）。原实现用 ÷1024，
// 导致同一容量在本面板里出现「976.6K」与下游「1000.0k」两种写法
// （用户反馈 2026-09-19「内容和顺序有点乱」）。
const formatTokens = (value: number): string =>
  value >= 1000 ? `${(value / 1000).toFixed(1)}K` : String(Math.round(value));

const ContextUsageRing: React.FC<ContextUsageRingProps> = ({
  tLimit,
  tUsed,
  tPct,
  tEffective,
  tBreakdown,
  usageSource,
  usageConfidence,
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

  // 分层色段：本地字典估算与 Provider 报数不同源，直接按估算值画到窗口刻度上
  // 会与标题百分比打架。故按 usedTokens / Σ(各来源) 缩放后再算段宽 ——
  // 色段之和恒等于「已使用」，与标题同口径。缺失/全 0 时回落单色条。
  const breakdownTotal = tBreakdown
    ? CONTEXT_SEGMENT_DEFS.reduce(
        (sum, def) => sum + Math.max(0, tBreakdown[def.key] ?? 0),
        0,
      )
    : 0;
  const scale = breakdownTotal > 0 && tUsed > 0 ? tUsed / breakdownTotal : 0;
  const segments =
    scale > 0 && tLimit > 0
      ? CONTEXT_SEGMENT_DEFS.map((def) => {
          const tokens = Math.max(0, (tBreakdown?.[def.key] ?? 0) * scale);
          return {
            ...def,
            tokens,
            percent: (tokens / tLimit) * 100,
          };
        }).filter((segment) => segment.tokens > 0)
      : [];
  const reservedTokens =
    tEffective && tEffective > 0 && tEffective < tLimit
      ? tLimit - tEffective
      : 0;
  const reservedPercent = tLimit > 0 ? (reservedTokens / tLimit) * 100 : 0;
  // 可用余量 = 窗口里既未被使用、也不属于预留的部分（条上表现为底色）。
  const availablePercent =
    tLimit > 0 ? Math.max(0, 100 - pct - reservedPercent) : 0;

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
            {segments.length > 0 ? (
              <>
                <div className={styles.contextUsagePanelProgress}>
                  {segments.map((segment) => (
                    <span
                      key={segment.key}
                      className={styles.contextUsagePanelSegment}
                      style={{
                        width: `${segment.percent}%`,
                        background: segment.color,
                      }}
                      title={`${segment.label} ${formatTokens(segment.tokens)}`}
                    />
                  ))}
                  {/* 系统预留固定贴条最右端（marginLeft:auto 吃掉中间的可用余量），
                      使「可用」与「不可用」在空间上一分为二。 */}
                  {reservedPercent > 0 && (
                    <span
                      className={styles.contextUsagePanelSegment}
                      style={{
                        width: `${reservedPercent}%`,
                        marginLeft: 'auto',
                        backgroundImage: CONTEXT_RESERVED_HATCH,
                      }}
                      title={`系统预留（不可用） ${formatTokens(reservedTokens)}`}
                    />
                  )}
                </div>
                <div
                  className={styles.contextUsagePanelLegend}
                  data-testid="context-usage-legend"
                >
                  {segments.map((segment) => (
                    <div
                      key={segment.key}
                      className={styles.contextUsagePanelLegendRow}
                    >
                      <span
                        className={styles.contextUsagePanelLegendDot}
                        style={{ background: segment.color }}
                      />
                      <span className={styles.contextUsagePanelLegendLabel}>
                        {segment.label}
                      </span>
                      <span className={styles.contextUsagePanelLegendValue}>
                        {segment.percent.toFixed(1)}%
                      </span>
                    </div>
                  ))}
                  {reservedPercent > 0 && (
                    <>
                      <div className={styles.contextUsagePanelLegendRow}>
                        <span
                          className={styles.contextUsagePanelLegendDot}
                          style={{
                            background: 'transparent',
                            border: CONTEXT_AVAILABLE_OUTLINE,
                          }}
                        />
                        <span className={styles.contextUsagePanelLegendLabel}>
                          可用余量
                        </span>
                        <span className={styles.contextUsagePanelLegendValue}>
                          {availablePercent.toFixed(1)}%
                        </span>
                      </div>
                      <div className={styles.contextUsagePanelLegendRow}>
                        <span
                          className={styles.contextUsagePanelLegendDot}
                          style={{ backgroundImage: CONTEXT_RESERVED_HATCH }}
                        />
                        <span className={styles.contextUsagePanelLegendLabel}>
                          系统预留（不可用）
                        </span>
                        <span className={styles.contextUsagePanelLegendValue}>
                          {reservedPercent.toFixed(1)}%
                        </span>
                      </div>
                    </>
                  )}
                </div>
              </>
            ) : (
              <div className={styles.contextUsagePanelProgress}>
                <span
                  className={styles.contextUsagePanelProgressBar}
                  style={{ width: `${pct}%`, background: color }}
                />
              </div>
            )}
            <div className={styles.contextUsagePanelBody}>
              {/* 本区只保留「运行状态块没覆盖」的信息：已使用/总量已在标题行，
                  剩余与缓存命中在同面板下方运行状态块里（且剩余是服务端口径：
                  已扣掉输出预算 reserve，与 tLimit-tUsed 并不相等 —— 这里不能
                  自己算一个与之打架的数字）。 */}
              {/* 数据来源：同一面板的百分比在不同回退源之间口径并不一致（DB 回退按
                  TotalTokens 含 completion，本地估算走字典），不标来源则「86% 但
                  剩余 0」这类组合无法判断。 */}
              {(usageConfidence || usageSource) && (
                <div className={styles.contextUsagePanelRow}>
                  <span className={styles.contextUsagePanelLabel}>数据来源</span>
                  <span className={styles.contextUsagePanelValue}>
                    {usageProvenanceLabel(usageConfidence, usageSource)}
                  </span>
                </div>
              )}
              {/* 分层明细只存在于「本进程内该会话发出过请求」时：内存快照按会话写入，
                  重启后或未发请求的会话六桶全 0，图例整块消失。这里明说原因，避免
                  被读成「功能丢失」（用户反馈 2026-09-19）。 */}
              {configured && segments.length === 0 && (
                <div className={styles.contextUsagePanelRow}>
                  <span className={styles.contextUsagePanelLabel}>分层明细</span>
                  <span className={styles.contextUsagePanelValue}>
                    暂不可用（本会话在当前进程内尚无请求记录）
                  </span>
                </div>
              )}
              {compactionStatus && (
                <div className={styles.contextUsagePanelRow}>
                  <span className={styles.contextUsagePanelLabel}>压缩</span>
                  <span className={styles.contextUsagePanelValue}>
                    {/* 只有真在压缩时才渲染呼吸点；「上次压缩 / 压缩失败」是终态事实，
                        不加任何动效，避免被读成「正在进行」（用户反馈 2026-09-19）。 */}
                    {compactionStatus === COMPACTION_RUNNING_LABEL && (
                      <span
                        className={styles.compactionStatusDot}
                        data-testid="compaction-status-dot"
                        aria-hidden="true"
                      />
                    )}
                    {compactionStatus}
                  </span>
                </div>
              )}
            </div>
          </>
        )}

        {/* 原 ComposerStatusDetails 弹层内容（运行摘要），整体并入；排在子代理入口
            之前，使面板按「上下文用量 → 运行状态 → 动作入口」的层级排列。 */}
        {runtimeDetails && (
          <div
            className={styles.contextUsagePanelRuntime}
            data-testid="context-usage-runtime"
          >
            {runtimeDetails}
          </div>
        )}

        {/* 原轻反馈带「子代理 N」入口：胶囊移除后在此保留可达性（动作类，置末）。 */}
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
              {' · '}打开管理器
            </span>
          </button>
        )}
      </div>
    ),
    [
      availablePercent,
      color,
      compactionStatus,
      configured,
      error,
      handleOpenSubAgents,
      onOpenSubAgents,
      pct,
      reservedPercent,
      runtimeDetails,
      segments,
      styles,
      subAgentsRunning,
      tLimit,
      tUsed,
      usageConfidence,
      usageSource,
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
