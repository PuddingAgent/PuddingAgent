// ── useGoal：ADR-074 Goal 持久控制面 G1 前端状态钩子 ────
// 状态来自服务端唯一投影（GET /api/v1/conversations/{id}/goal）；
// 本 hook 不从聊天文本反推 Goal 状态。

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
  const requestSeqRef = useRef(0);

  const refresh = useCallback(async () => {
    const seq = ++requestSeqRef.current;
    if (!enabled || !workspaceId || !conversationId) {
      setGoal(null);
      setError(null);
      setLoading(false);
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
    } catch (err) {
      if (seq !== requestSeqRef.current) return;
      setGoal(null);
      setError(err instanceof Error ? err.message : 'Goal 状态读取失败');
    } finally {
      if (seq === requestSeqRef.current) setLoading(false);
    }
  }, [enabled, workspaceId, conversationId]);

  useEffect(() => {
    void refresh();
  }, [refresh]);

  const runCommand = useCallback<UseGoalResult['runCommand']>(
    async (action, options) => {
      if (!workspaceId || !conversationId || !agentId) {
        return '缺少 workspace / conversation / agent，无法执行 Goal 命令。';
      }
      setCommandRunning(true);
      try {
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
        if (response.goal) setGoal(response.goal);
        else void refresh();
        return response.message;
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
