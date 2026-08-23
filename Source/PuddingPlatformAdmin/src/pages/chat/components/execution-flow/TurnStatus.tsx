// ── TurnStatus：单行运行态（CU-05，对齐消息 UI §5.1 / split-plan CU-05）────────
// 收敛 WaitingBubble / CurrentActivityPanel 的重复状态区域：
//  - kind 由 canonical 事件字段派生（pending/running/succeeded/failed/cancelled）
//  - 文案来自已知事实（正在连接模型/正在推理/正在执行工具/正在等待子代理/正在生成回答），
//    无可见事件时显示「{agentName} 正在运行」（默认「默认助手」）
//  - 不展示「复杂推理/深入分析」等推断文案
//  - 运行 ≥15s 才显示计时；基于持久化 turnStartedAt（reload/重挂载不归零）
//  - 唯一 aria-live="polite" 状态区；终态到达立即不渲染（错误由终态错误行负责）
import React, { useEffect, useState } from 'react';
import type { ExecutionFlowProjection } from '../../projections/executionFlowProjector';
import { useExecutionFlowStyles } from '../../styles/execution-flow.styles';
import { ExecutionDisclosureRow } from './ExecutionDisclosureRow';
import { TurnStatusOrb } from './TurnStatusOrb';

// ── TurnStatus 类型（canonical 事件字段派生）───────────────────────────────

export type TurnStatusKind =
  | 'pending'
  | 'running'
  | 'succeeded'
  | 'failed'
  | 'cancelled';

/** 运行中阶段：全部来自已知事实，不推断模型内心状态。 */
export type TurnPhase =
  | 'connecting'
  | 'reasoning'
  | 'executing'
  | 'delegating'
  | 'answering';

export interface TurnStatus {
  kind: TurnStatusKind;
  /** running 时有效：当前阶段（来自已知事实）。 */
  phase?: TurnPhase;
  /** 终态事件 canonical id（派生自投影终态节点）。 */
  terminalEventId?: string;
}

/** 阶段文案（§5.1 固定五类）。 */
export const TURN_STATUS_PHASE_COPY: Record<TurnPhase, string> = {
  connecting: '正在连接模型',
  reasoning: '正在推理',
  executing: '正在执行工具',
  delegating: '正在等待子代理',
  answering: '正在生成回答',
};

const TERMINAL_KINDS: ReadonlySet<TurnStatusKind> = new Set([
  'succeeded',
  'failed',
  'cancelled',
]);

/**
 * 从 CU-04 ExecutionFlowProjection 输出派生 TurnStatus（canonical 路径）：
 * 终态节点 → succeeded/failed/cancelled；无节点 → pending；
 * 最后节点 kind → 对应阶段；retry/未知节点 → connecting（重连/等待模型）。
 */
export function deriveTurnStatusFromProjection(
  projection: ExecutionFlowProjection,
): TurnStatus {
  const { terminal, nodes } = projection;
  if (terminal) {
    return {
      kind:
        terminal.state === 'completed'
          ? 'succeeded'
          : terminal.state === 'failed'
            ? 'failed'
            : 'cancelled',
      terminalEventId: terminal.firstEventId,
    };
  }
  const last = nodes[nodes.length - 1];
  if (!last) return { kind: 'pending' };
  const phase: TurnPhase =
    last.kind === 'reasoning'
      ? 'reasoning'
      : last.kind === 'tool'
        ? 'executing'
        : last.kind === 'delegation'
          ? 'delegating'
          : last.kind === 'message'
            ? 'answering'
            : 'connecting';
  return { kind: 'running', phase };
}

/** 消息渲染层的已知事实（AgentMessageBubble 消费点输入）。 */
export interface TurnStatusFacts {
  /** turn 是否仍在运行（false = 终态已落定，TurnStatus 不渲染）。 */
  active: boolean;
  /** 是否有可见执行事件（reasoning/tool/delegation/answer 内容等）。 */
  hasVisibleEvents: boolean;
  /** 最近活动阶段；有可见事件但阶段未知时回落 connecting。 */
  phase?: TurnPhase;
}

/** 从消息渲染层已知事实派生 TurnStatus（非投影完整输入时的消费点路径）。 */
export function deriveTurnStatusFromFacts(facts: TurnStatusFacts): TurnStatus {
  if (!facts.active) return { kind: 'succeeded' };
  if (!facts.hasVisibleEvents) return { kind: 'pending' };
  return { kind: 'running', phase: facts.phase ?? 'connecting' };
}

// ── TurnStatus 组件 ────────────────────────────────────────────────────────

/** 运行时钟显示阈值（秒）；不足 15s 不显示计时（§5.1）。 */
export const TURN_STATUS_CLOCK_THRESHOLD_SECONDS = 15;

/** 「Xs」/「Xm」：<60s 显示秒，≥60s 取整分钟（与 WaitingBubble 同格式）。 */
function formatElapsed(seconds: number): string {
  return seconds < 60 ? `${seconds}s` : `${Math.floor(seconds / 60)}m`;
}

export interface TurnStatusProps {
  /** 派生后的状态（terminal 时不渲染）。 */
  status: TurnStatus;
  /** 持久化 turn 起点（毫秒时间戳；reload/重挂载不归零）。 */
  turnStartedAt: number;
  /** 展示名；pending 态显示「{agentName} 正在运行」，默认「默认助手」。 */
  agentName?: string;
  /** 计时显示阈值（秒），默认 15。 */
  clockThresholdSeconds?: number;
  /** 测试注入当前时间；未传时内部每秒 tick。 */
  now?: number;
}

export const TurnStatus: React.FC<TurnStatusProps> = ({
  status,
  turnStartedAt,
  agentName = '默认助手',
  clockThresholdSeconds = TURN_STATUS_CLOCK_THRESHOLD_SECONDS,
  now: nowProp,
}) => {
  const { styles } = useExecutionFlowStyles();
  const [now, setNow] = useState(() => nowProp ?? Date.now());

  useEffect(() => {
    if (nowProp !== undefined) return undefined;
    const timer = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(timer);
  }, [nowProp]);

  // 终态到达立即移除；错误/取消由终态错误行负责展示。
  if (TERMINAL_KINDS.has(status.kind)) return null;

  const current = nowProp ?? now;
  const elapsedSeconds =
    Number.isFinite(turnStartedAt) && turnStartedAt > 0
      ? Math.max(0, Math.floor((current - turnStartedAt) / 1000))
      : 0;
  const showClock = elapsedSeconds >= clockThresholdSeconds;
  const label =
    status.kind === 'pending'
      ? `${agentName} 正在运行`
      : TURN_STATUS_PHASE_COPY[status.phase ?? 'connecting'];

  return (
    <ExecutionDisclosureRow
      leading={
        <TurnStatusOrb
          pending={status.kind === 'pending'}
          phase={status.phase}
          ariaLabel={label}
        />
      }
      testId="turn-status"
      ariaLive="polite"
      className={styles.turnStatusRow}
    >
      <span className={styles.turnStatusLabel} data-testid="turn-status-label">
        {label}
      </span>
      {showClock && (
        <span
          className={styles.turnStatusElapsed}
          data-testid="turn-status-elapsed"
        >
          {/* 回答正在流式产出时是「运行」而非「等待」；等待语义保留给连接/排队阶段。 */}
          · {status.phase === 'answering' ? '已运行' : '已等待'}{' '}
          {formatElapsed(elapsedSeconds)}
        </span>
      )}
    </ExecutionDisclosureRow>
  );
};

export default React.memo(TurnStatus);
