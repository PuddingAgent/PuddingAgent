// ── useGoal：ADR-074 Goal 持久控制面 G1 前端状态钩子 ────
// 状态来自服务端唯一投影（GET /api/v1/conversations/{id}/goal）；
// 本 hook 不从聊天文本反推 Goal 状态。
// Goal 不在 SSE 流上：后台 worker 驱动的 phase 跃迁（如自动续行、结算）
// 不会推送前端 ⇒ 活跃 Goal 以 3~5 秒自适应轮询保持新鲜；
// 进入终态（含 blocked）立即停止；卸载/切会话时清理定时器。

import { useCallback, useEffect, useRef, useState } from 'react';
import {
  executeGoalCommand,
  type GoalAction,
  type GoalSnapshot,
  getConversationGoal,
} from '@/services/platform/api';

interface UseGoalOptions {
  workspaceId?: string;
  conversationId?: string;
  agentId?: string;
  /** 首屏关键内容完成前可关闭辅助 Goal 查询。 */
  enabled?: boolean;
}

interface UseGoalResult {
  goal: GoalSnapshot | null;
  loading: boolean;
  error: string | null;
  commandRunning: boolean;
  refresh: () => Promise<void>;
  runCommand: (
    action: GoalAction,
    options?: {
      objective?: string;
      rounds?: number;
      reason?: string;
      expectedVersion?: number;
    },
  ) => Promise<string>;
}

const TERMINAL_PHASES = new Set([
  'completed',
  'cancelled',
  'failed',
  'budget_exhausted',
]);

export function isTerminalGoalPhase(phase: GoalSnapshot['phase']): boolean {
  return TERMINAL_PHASES.has(phase);
}

/**
 * 轮询停止相位：终态 + blocked。
 * blocked 不属于终态，但它的解除需要人工/门禁介入而非后台 worker 跃迁，
 * 持续轮询无增益，故一并停止（UI 上仍可通过命令按钮主动刷新）。
 */
const POLL_STOP_PHASES = new Set<string>([...TERMINAL_PHASES, 'blocked']);

export function shouldStopGoalPolling(phase: GoalSnapshot['phase']): boolean {
  return POLL_STOP_PHASES.has(phase);
}

/** 自适应轮询节奏：命令/激活后 3s 起步，每轮 +1s，上限 5s。 */
const POLL_MIN_MS = 3000;
const POLL_MAX_MS = 5000;
const POLL_BACKOFF_STEP_MS = 1000;
/** 轮询连续失败重试上限（网络抖动自愈；超过后停止，等待命令/重挂载触发）。 */
const POLL_ERROR_RETRY_LIMIT = 3;

export function useGoal({
  workspaceId,
  conversationId,
  agentId,
  enabled = true,
}: UseGoalOptions): UseGoalResult {
  const [goal, setGoal] = useState<GoalSnapshot | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [commandRunning, setCommandRunning] = useState(false);
  const [pollErrorTick, setPollErrorTick] = useState(0);
  const requestSeqRef = useRef(0);
  const pollDelayRef = useRef(POLL_MIN_MS);
  const pollErrorCountRef = useRef(0);
  // 供命令回调读取最新快照（闭包内 state 会过期）。
  const goalRef = useRef<GoalSnapshot | null>(null);
  goalRef.current = goal;

  const refresh = useCallback(async () => {
    const seq = ++requestSeqRef.current;
    if (!enabled || !workspaceId || !conversationId) {
      setGoal(null);
      setError(null);
      setLoading(false);
      pollErrorCountRef.current = 0;
      setPollErrorTick(0);
      return;
    }
    setLoading(true);
    try {
      const { goal: snapshot } = await getConversationGoal(
        workspaceId,
        conversationId,
      );
      if (seq !== requestSeqRef.current) return; // 过期响应（会话已切换）
      setGoal(snapshot);
      setError(null);
      pollErrorCountRef.current = 0;
      setPollErrorTick(0);
    } catch (err) {
      if (seq !== requestSeqRef.current) return;
      setGoal(null);
      setError(err instanceof Error ? err.message : 'Goal 状态读取失败');
      // 轮询失败重试：有界自愈，超过上限后停止（tick 置 -1 表示停轮询）。
      pollErrorCountRef.current += 1;
      setPollErrorTick(
        pollErrorCountRef.current <= POLL_ERROR_RETRY_LIMIT
          ? pollErrorCountRef.current
          : -1,
      );
    } finally {
      if (seq === requestSeqRef.current) setLoading(false);
    }
  }, [enabled, workspaceId, conversationId]);

  useEffect(() => {
    void refresh();
  }, [refresh]);

  // ── 自适应轮询：仅活跃（非终态且非 blocked）Goal 需要跟踪后台跃迁 ──
  useEffect(() => {
    if (!enabled || !workspaceId || !conversationId) return;
    if (goal) {
      if (shouldStopGoalPolling(goal.phase)) return; // 终态/blocked：立即停止
    } else if (pollErrorTick === 0 || pollErrorTick < 0) {
      // 无 Goal 且非「失败重试」状态：无轮询对象
      return;
    }
    const timer = window.setTimeout(() => {
      pollDelayRef.current = Math.min(
        pollDelayRef.current + POLL_BACKOFF_STEP_MS,
        POLL_MAX_MS,
      );
      void refresh();
    }, pollDelayRef.current);
    return () => window.clearTimeout(timer);
  }, [goal, pollErrorTick, enabled, workspaceId, conversationId, refresh]);

  const runCommand = useCallback<UseGoalResult['runCommand']>(
    async (action, options) => {
      if (!workspaceId || !conversationId || !agentId) {
        return '缺少 workspace / conversation / agent，无法执行 Goal 命令。';
      }
      setCommandRunning(true);
      pollDelayRef.current = POLL_MIN_MS; // 命令后以最短间隔观察 phase 跃迁
      try {
        const before = goalRef.current;
        const response = await executeGoalCommand(workspaceId, conversationId, {
          agentId,
          clientRequestId: `goal-${Date.now()}-${Math.random()
            .toString(36)
            .slice(2, 8)}`,
          action,
          objective: options?.objective,
          rounds: options?.rounds,
          reason: options?.reason,
          expectedVersion: options?.expectedVersion,
        });
        let resultMessage = response.message;
        if (action === 'extend') {
          // 历史服务端对未知 action 会「静默降级为 status 且返回 200」，
          // 因此不能只看 HTTP 200/success：必须核对响应体中的 Goal 语义
          // （maxIterations 增长，或 budget_exhausted 被解除）才算确认生效。
          const after = response.goal ?? null;
          const confirmed =
            !!after &&
            !!before &&
            (after.maxIterations > before.maxIterations ||
              (before.phase === 'budget_exhausted' &&
                after.phase !== 'budget_exhausted'));
          if (!confirmed) {
            resultMessage =
              '延长额度未确认生效：服务端响应不含 extend 执行语义（旧版本会把未知 action 静默降级为状态查询）。请确认后端已部署 extend 动作后重试。';
          }
        }
        if (response.goal) setGoal(response.goal);
        void refresh(); // 命令返回后立即刷新一次
        return resultMessage;
      } catch (err) {
        return err instanceof Error ? err.message : 'Goal 命令执行失败';
      } finally {
        setCommandRunning(false);
      }
    },
    [workspaceId, conversationId, agentId, refresh],
  );

  return { goal, loading, error, commandRunning, refresh, runCommand };
}
