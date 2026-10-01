// ── ComposerStatusDetails：运行状态详情（面向普通用户的摘要）──
import { CodeOutlined } from '@ant-design/icons';
import React from 'react';
import type { TurnTimings } from '@/services/platform/api';
import { useChatStyles } from '../styles';
import type { ChatStatus } from './InputArea';
import type {
  BackgroundMemoryStatus,
  ContextServiceStatus,
  IndexServiceStatus,
  ModelServiceStatus,
} from './serviceStatus';
import TurnTimingPanel from './TurnTimingPanel';

/** 缓存命中率的**口径**：窗口 vs 全会话（两者数值可能不同，必须标注是哪一个）。 */
export type CacheHitRateScope = 'windowed' | 'session';

/** Composer 运行时摘要视图模型 */
export interface ComposerRuntimeSummary {
  status: ChatStatus;
  statusLabel: string;
  token?: {
    used: number;
    limit: number;
    /** 有效输入窗口（模型窗口 − 预留输出）；剩余与百分比的口径依据。 */
    effectiveLimit?: number;
    percentage: number;
    remaining?: number;
  };
  /** 上下文详情打开时会按需刷新；没有 token 数据时用该状态解释当前空白。 */
  contextUsageStatus?: 'idle' | 'loading' | 'ready' | 'error';
  /** 上下文窗口解析失败时展示后端返回的诊断文本。 */
  contextUsageError?: string;
  /** 缓存命中率，统一使用 **0-100 百分比**口径（后端 `TTL` 契约是 0-1 比例，换算在上游）。 */
  cacheHitRate?: number;
  /** 该命中率的口径；`undefined` ⇒ 不知道来自哪个口径，不标注。 */
  cacheHitRateScope?: CacheHitRateScope;
  /** `windowed` 口径的样本条数（后端 `analyzedEventCount`）。 */
  cacheHitRateSampleCount?: number;
  /** 上下文服务（ASP/LSP 的普通模式翻译）；`unknown` = 没采到，不是「可用」。 */
  contextService: ContextServiceStatus;
  /** 索引状态；`unknown` = 探测不可用（**不冒充** available/disabled）。 */
  index: IndexServiceStatus;
  /** 后台记忆整理；`unknown` = 状态不可知（如调试端点 404）。 */
  backgroundMemory: BackgroundMemoryStatus;
  /** 当前会话可见的子任务数 */
  subAgentsRunning: number;
  /** 模型服务 */
  modelService: ModelServiceStatus;
  /** 本 Turn 的后端耗时明细（`turn.completed` 帧携带）；未采集 ⇒ 该面板不渲染。 */
  turnTimings?: TurnTimings;
  /** 最近一次 usage（输入/输出 token）；未采集 ⇒ 面板按「未采集」渲染。 */
  usage?: { promptTokens?: number; completionTokens?: number };
}

interface ComposerStatusDetailsProps {
  summary: ComposerRuntimeSummary;
  onOpenDevDetails?: () => void;
}

/** 状态文案映射 */
const STATUS_LABEL: Record<ComposerRuntimeSummary['status'], string> = {
  idle: '就绪',
  initializing: '正在初始化…',
  composing: '输入中…',
  // 「整理上下文」会与压缩共用同一批词，用户无法分辨 → 统一改成思考。
  thinking: '正在思考…',
  tool_executing: '正在调用工具…',
  streaming: '正在生成回复…',
  completed: '已完成',
  error: '出错了，可重试',
};

/** 服务状态 → 圆点色（IMG01：改用 §3 语义 token，不再用暖灰/暖琥珀字面量） */
const SERVICE_COLOR: Record<string, string> = {
  available: 'var(--pudding-status-success)',
  idle: 'var(--pudding-chat-border-strong)',
  running: 'var(--pudding-status-success)',
  building: 'var(--pudding-status-warning)',
  warning: 'var(--pudding-status-warning)',
  error: 'var(--pudding-status-error)',
  disabled: 'var(--pudding-chat-border-strong)',
  // `unknown`（没采到）沿用 `disabled` 的中性灰：它既不是故障，也不是「关」。
  unknown: 'var(--pudding-chat-border-strong)',
};

/** 服务状态 → 文案 */
const SERVICE_LABEL: Record<string, string> = {
  available: '可用',
  idle: '待机',
  running: '运行中',
  building: '建立中',
  warning: '需注意',
  error: '异常',
  disabled: '未启用',
  // 「没采到」必须与「未启用」不同文案，否则不可知会被读成关。
  unknown: '未知',
};

/** 格式化 Token 数 */
const fmtTokens = (n: number): string => {
  if (n >= 1000) return `${(n / 1000).toFixed(1)}k`;
  return String(n);
};

const fmtCacheHitRate = (rate?: number): string => {
  if (rate === undefined || !Number.isFinite(rate) || rate < 0) return '待计算';
  return `${Math.min(100, Math.round(rate))}%`;
};

/**
 * 缓存命中率**口径**标注：窗口（带样本条数）vs 全会话。
 *
 * 两个口径的数值可能不同（窗口只看最近 N 条事件，会话是累计），所以必须把
 * 「当前显示的是哪一个」写在数值旁边，而不是让窗口值静默覆盖会话值。
 * 口径未知（老调用点没传）⇒ 不标注，也不猜。
 */
export const formatCacheHitRateScope = (
  scope?: CacheHitRateScope,
  sampleCount?: number,
): string | undefined => {
  if (scope === 'windowed') {
    return typeof sampleCount === 'number' && Number.isFinite(sampleCount)
      ? `本请求窗口(最近${sampleCount}条)`
      : '本请求窗口';
  }
  if (scope === 'session') return '全会话';
  return undefined;
};

const getContextUsageFallbackLabel = (
  status?: ComposerRuntimeSummary['contextUsageStatus'],
  error?: string,
): string => {
  if (status === 'loading') return '正在刷新…';
  if (status === 'error') return error || '刷新失败';
  return '待刷新';
};

const ComposerStatusDetails: React.FC<ComposerStatusDetailsProps> = ({
  summary,
  onOpenDevDetails,
}) => {
  const { styles } = useChatStyles();
  const remainingTokens = summary.token
    ? (summary.token.remaining ??
      Math.max(summary.token.limit - summary.token.used, 0))
    : undefined;
  // 剩余是按服务端 effectiveWindowTokens 算出来的，分母必须同口径，否则会出现
  // 「占用 9.7% / 剩余只剩一半」并存（用户反馈 2026-09-19）。
  const windowLimit = summary.token?.limit ?? 0;
  const effectiveLimit =
    summary.token?.effectiveLimit && summary.token.effectiveLimit > 0
      ? summary.token.effectiveLimit
      : windowLimit;
  const reservedOutput = Math.max(windowLimit - effectiveLimit, 0);
  const cacheHitScopeLabel = formatCacheHitRateScope(
    summary.cacheHitRateScope,
    summary.cacheHitRateSampleCount,
  );

  return (
    <div className={styles.composerStatusDetails}>
      {/* ── 状态标题行 ── */}
      <div className={styles.composerStatusDetailsHeader}>
        <span
          className={styles.composerStatusDetailsDot}
          style={{
            background:
              summary.status === 'error'
                ? 'var(--pudding-status-error)'
                : summary.status === 'streaming' ||
                    summary.status === 'thinking' ||
                    summary.status === 'tool_executing'
                  ? 'var(--pudding-status-success)'
                  : 'var(--pudding-chat-border-strong)',
          }}
        />
        <span className={styles.composerStatusDetailsTitle}>
          {STATUS_LABEL[summary.status] ?? '就绪'}
        </span>
      </div>

      {/* ── 本轮摘要 ── */}
      <div className={styles.composerStatusDetailsGroup}>
        <div className={styles.composerStatusDetailsGroupTitle}>本轮摘要</div>
        {summary.token && summary.token.limit > 0 && (
          <>
            <div className={styles.composerStatusDetailRow}>
              <span className={styles.composerStatusDetailLabel}>有效上下文</span>
              <span className={styles.composerStatusDetailValue}>
                剩余 {fmtTokens(Math.max(remainingTokens ?? 0, 0))} /{' '}
                {fmtTokens(effectiveLimit)}
              </span>
            </div>
            {reservedOutput > 0 && (
              <div className={styles.composerStatusDetailRow}>
                <span className={styles.composerStatusDetailLabel}>预留输出</span>
                <span className={styles.composerStatusDetailValue}>
                  {fmtTokens(reservedOutput)}
                </span>
              </div>
            )}
          </>
        )}
        {(!summary.token || summary.token.limit <= 0) && (
          <div className={styles.composerStatusDetailRow}>
            <span className={styles.composerStatusDetailLabel}>有效上下文</span>
            <span className={styles.composerStatusDetailValue}>
              {getContextUsageFallbackLabel(
                summary.contextUsageStatus,
                summary.contextUsageError,
              )}
            </span>
          </div>
        )}
        <div className={styles.composerStatusDetailRow}>
          <span className={styles.composerStatusDetailLabel}>缓存命中</span>
          <span className={styles.composerStatusDetailValue}>
            <span data-testid="cache-hit-rate">
              {fmtCacheHitRate(summary.cacheHitRate)}
            </span>
            {cacheHitScopeLabel !== undefined && (
              <span style={{ marginLeft: 6, opacity: 0.7 }}>
                {cacheHitScopeLabel}
              </span>
            )}
          </span>
        </div>
        <div className={styles.composerStatusDetailRow}>
          <span className={styles.composerStatusDetailLabel}>上下文服务</span>
          <span className={styles.composerStatusDetailValue}>
            <span
              className={styles.composerStatusDetailsDot}
              style={{
                background: SERVICE_COLOR[summary.contextService] ?? 'var(--pudding-chat-border-strong)',
                width: 6,
                height: 6,
                display: 'inline-block',
                borderRadius: '50%',
                marginRight: 4,
                verticalAlign: 'middle',
              }}
            />
            {SERVICE_LABEL[summary.contextService] ?? '未知'}
          </span>
        </div>
        <div className={styles.composerStatusDetailRow}>
          <span className={styles.composerStatusDetailLabel}>后台记忆整理</span>
          <span className={styles.composerStatusDetailValue}>
            <span
              className={styles.composerStatusDetailsDot}
              style={{
                background:
                  SERVICE_COLOR[summary.backgroundMemory] ?? 'var(--pudding-chat-border-strong)',
                width: 6,
                height: 6,
                display: 'inline-block',
                borderRadius: '50%',
                marginRight: 4,
                verticalAlign: 'middle',
              }}
            />
            {SERVICE_LABEL[summary.backgroundMemory] ?? '未知'}
          </span>
        </div>
        <div className={styles.composerStatusDetailRow}>
          <span className={styles.composerStatusDetailLabel}>索引</span>
          <span className={styles.composerStatusDetailValue}>
            <span
              className={styles.composerStatusDetailsDot}
              style={{
                background: SERVICE_COLOR[summary.index] ?? 'var(--pudding-chat-border-strong)',
                width: 6,
                height: 6,
                display: 'inline-block',
                borderRadius: '50%',
                marginRight: 4,
                verticalAlign: 'middle',
              }}
            />
            {SERVICE_LABEL[summary.index] ?? '未知'}
          </span>
        </div>
        {summary.subAgentsRunning > 0 && (
          <div className={styles.composerStatusDetailRow}>
            <span className={styles.composerStatusDetailLabel}>子任务</span>
            <span className={styles.composerStatusDetailValue}>
              {summary.subAgentsRunning} 个
            </span>
          </div>
        )}
        <div className={styles.composerStatusDetailRow}>
          <span className={styles.composerStatusDetailLabel}>模型服务</span>
          <span className={styles.composerStatusDetailValue}>
            <span
              className={styles.composerStatusDetailsDot}
              style={{
                background: SERVICE_COLOR[summary.modelService] ?? 'var(--pudding-chat-border-strong)',
                width: 6,
                height: 6,
                display: 'inline-block',
                borderRadius: '50%',
                marginRight: 4,
                verticalAlign: 'middle',
              }}
            />
            {SERVICE_LABEL[summary.modelService] ?? '未知'}
          </span>
        </div>
        {/* 本轮耗时明细（后端 timing 契约）：独立组件承载，缺席一律「未采集」。 */}
        <TurnTimingPanel timings={summary.turnTimings} usage={summary.usage} />
      </div>

      {/* ── 开发者详情入口 ── */}
      {onOpenDevDetails && (
        <div className={styles.composerStatusDetailsDevEntry}>
          <button
            type="button"
            className={styles.composerStatusDetailsDevButton}
            onClick={onOpenDevDetails}
            aria-label="打开开发者详情"
          >
            <CodeOutlined />
            <span>打开开发者详情</span>
          </button>
        </div>
      )}
    </div>
  );
};

export default ComposerStatusDetails;
