// ── GoalBanner：ADR-074 G1 最小 Goal 状态条 ────
// 展示 objective / phase / iteration 进度与 pause/resume/cancel 控件。
// 权威状态始终来自服务端投影；本组件不解析 presentation 文本驱动按钮。

import {
  CaretRightOutlined,
  PauseOutlined,
  StopOutlined,
  ThunderboltOutlined,
} from '@ant-design/icons';
import { Alert, Button, Space, Tooltip, message } from 'antd';
import React from 'react';
import type { GoalSnapshot } from '@/services/platform/api';
import { isTerminalGoalPhase } from '../hooks/useGoal';

interface GoalBannerProps {
  goal: GoalSnapshot | null;
  commandRunning: boolean;
  onCommand: (
    action: 'pause' | 'resume' | 'cancel',
    options?: { reason?: string },
  ) => Promise<string>;
}

const PHASE_TEXT: Record<GoalSnapshot['phase'], string> = {
  active: '运行中',
  paused: '已暂停',
  blocked: '受阻',
  budget_exhausted: '额度耗尽',
  completed: '已完成',
  cancelled: '已取消',
  failed: '失败',
};

function phaseAlertType(phase: GoalSnapshot['phase']) {
  if (phase === 'active') return 'info' as const;
  if (phase === 'paused' || phase === 'blocked') return 'warning' as const;
  return 'success' as const;
}

const GoalBanner: React.FC<GoalBannerProps> = ({
  goal,
  commandRunning,
  onCommand,
}) => {
  const [messageApi, contextHolder] = message.useMessage();
  if (!goal) return null;

  const terminal = isTerminalGoalPhase(goal.phase);
  const progress = `${goal.iterationsStarted}/${goal.maxIterations}`;
  const header = (
    <Space size={12} wrap align="center">
      <ThunderboltOutlined />
      <span>
        Goal {PHASE_TEXT[goal.phase] ?? goal.phase} · Iteration {progress}
      </span>
      <span style={{ opacity: 0.85, fontWeight: 400 }}>{goal.objective}</span>
    </Space>
  );

  const run = async (
    action: 'pause' | 'resume' | 'cancel',
    reason?: string,
  ) => {
    const text = await onCommand(action, reason ? { reason } : undefined);
    void messageApi.info(text);
  };

  return (
    <Alert
      type={phaseAlertType(goal.phase)}
      banner
      showIcon={false}
      style={{ borderRadius: 0 }}
      message={header}
      description={
        <Space size={4} wrap>
          {!terminal && goal.phase === 'active' && (
            <Tooltip title="暂停自主续行；已消费 Iteration 保留">
              <Button
                size="small"
                icon={<PauseOutlined />}
                disabled={commandRunning}
                onClick={() => void run('pause')}
              >
                暂停
              </Button>
            </Tooltip>
          )}
          {!terminal && goal.phase !== 'active' && (
            <Tooltip title="恢复自主续行（不重置已消费额度）">
              <Button
                size="small"
                icon={<CaretRightOutlined />}
                disabled={commandRunning}
                onClick={() => void run('resume')}
              >
                恢复
              </Button>
            </Tooltip>
          )}
          {!terminal && (
            <Tooltip title="取消 Goal（可审计终态；事件与证据保留）">
              <Button
                size="small"
                danger
                icon={<StopOutlined />}
                disabled={commandRunning}
                onClick={() => void run('cancel', 'user_cancel_from_banner')}
              >
                取消
              </Button>
            </Tooltip>
          )}
          {goal.statusReason && (
            <span style={{ opacity: 0.7 }}>原因：{goal.statusReason}</span>
          )}
          {terminal && goal.terminalAtUtc && (
            <span style={{ opacity: 0.6 }}>
              终止于 {new Date(goal.terminalAtUtc).toLocaleString()}
            </span>
          )}
          {contextHolder}
        </Space>
      }
    />
  );
};

export default GoalBanner;
