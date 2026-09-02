// ── GoalBanner：ADR-074 Goal 顶部状态入口 ─────────────────────────────
// 默认只占用 Header 中一个紧凑按钮；完整 objective / reason / controls
// 仅在 hover 或 click 后的 Popover 中展示，避免长 Task Goal 挤占会话空间。

import {
  CaretRightOutlined,
  PauseOutlined,
  PlusOutlined,
  StopOutlined,
  ThunderboltOutlined,
} from '@ant-design/icons';
import {
  Button,
  Divider,
  Form,
  Input,
  InputNumber,
  message,
  Modal,
  Popconfirm,
  Popover,
  Space,
  Tooltip,
} from 'antd';
import React, { useState } from 'react';
import type { GoalAction, GoalSnapshot } from '@/services/platform/api';
import { isTerminalGoalPhase } from '../hooks/useGoal';

interface GoalBannerProps {
  goal: GoalSnapshot | null;
  commandRunning: boolean;
  onCommand: (
    action: GoalAction,
    options?: {
      objective?: string;
      rounds?: number;
      reason?: string;
      expectedVersion?: number;
    },
  ) => Promise<string>;
}

interface GoalStartValues {
  objective: string;
  rounds: number;
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

const PHASE_TONE: Record<
  GoalSnapshot['phase'],
  { color: string; background: string; borderColor: string }
> = {
  active: {
    color: '#1677ff',
    background: 'rgba(22, 119, 255, 0.10)',
    borderColor: 'rgba(22, 119, 255, 0.36)',
  },
  paused: {
    color: '#d48806',
    background: 'rgba(250, 173, 20, 0.12)',
    borderColor: 'rgba(250, 173, 20, 0.40)',
  },
  blocked: {
    color: '#d46b08',
    background: 'rgba(250, 140, 22, 0.12)',
    borderColor: 'rgba(250, 140, 22, 0.40)',
  },
  budget_exhausted: {
    color: '#cf1322',
    background: 'rgba(255, 77, 79, 0.10)',
    borderColor: 'rgba(255, 77, 79, 0.36)',
  },
  completed: {
    color: '#389e0d',
    background: 'rgba(82, 196, 26, 0.10)',
    borderColor: 'rgba(82, 196, 26, 0.34)',
  },
  cancelled: {
    color: 'var(--pudding-chat-text-subtle)',
    background: 'var(--pudding-chat-surface-muted)',
    borderColor: 'var(--pudding-chat-border)',
  },
  failed: {
    color: '#cf1322',
    background: 'rgba(255, 77, 79, 0.10)',
    borderColor: 'rgba(255, 77, 79, 0.36)',
  },
};

const firstObjectiveLine = (objective: string) =>
  objective
    .split(/\r?\n/)
    .map((line) => line.trim())
    .find(Boolean) ?? '未命名 Goal';

const GoalBanner: React.FC<GoalBannerProps> = ({
  goal,
  commandRunning,
  onCommand,
}) => {
  const [messageApi, contextHolder] = message.useMessage();
  const [startOpen, setStartOpen] = useState(false);
  const [startForm] = Form.useForm<GoalStartValues>();

  const startGoal = async () => {
    let values: GoalStartValues;
    try {
      values = await startForm.validateFields();
    } catch {
      return;
    }
    const text = await onCommand('set', {
      objective: values.objective.trim(),
      rounds: values.rounds,
    });
    void messageApi.info(text);
    setStartOpen(false);
    startForm.resetFields();
  };

  const startModal = (
    <Modal
      title="开始 Goal"
      open={startOpen}
      okText="开始"
      cancelText="取消"
      confirmLoading={commandRunning}
      onOk={() => void startGoal()}
      onCancel={() => setStartOpen(false)}
      destroyOnHidden
    >
      <Form
        form={startForm}
        layout="vertical"
        initialValues={{ rounds: 32 }}
        preserve={false}
      >
        <Form.Item
          name="objective"
          label="目标"
          rules={[
            { required: true, whitespace: true, message: '请输入 Goal 目标' },
            { max: 4000, message: '目标最多 4000 个字符' },
          ]}
        >
          <Input.TextArea
            rows={5}
            autoFocus
            placeholder="描述要持续完成的目标、约束和验收条件"
          />
        </Form.Item>
        <Form.Item
          name="rounds"
          label="最大 Iteration"
          rules={[{ required: true, message: '请输入 Iteration 上限' }]}
          extra="每次恢复不会重置已消费额度；达到上限后必须新建 Goal。"
        >
          <InputNumber min={1} max={256} precision={0} style={{ width: 160 }} />
        </Form.Item>
      </Form>
    </Modal>
  );

  if (!goal) {
    return (
      <>
        {contextHolder}
        <Button
          size="small"
          icon={<PlusOutlined />}
          disabled={commandRunning}
          onClick={() => setStartOpen(true)}
          aria-label="开始 Goal"
          style={{ height: 28, borderRadius: 999, flexShrink: 0 }}
        >
          Goal
        </Button>
        {startModal}
      </>
    );
  }

  const terminal = isTerminalGoalPhase(goal.phase);
  const progress = `${goal.iterationsStarted}/${goal.maxIterations}`;
  const phaseText = PHASE_TEXT[goal.phase] ?? goal.phase;
  const objectiveSummary = firstObjectiveLine(goal.objective);
  const tone = PHASE_TONE[goal.phase];

  const run = async (
    action: 'pause' | 'resume' | 'cancel',
    reason?: string,
  ) => {
    const text = await onCommand(action, reason ? { reason } : undefined);
    void messageApi.info(text);
  };

  const details = (
    <div
      role="dialog"
      aria-label="Goal 详情"
      style={{
        width: 'min(560px, calc(100vw - 48px))',
        maxWidth: '100%',
      }}
    >
      <div
        style={{
          marginBottom: 8,
          color: 'var(--pudding-chat-text)',
          fontSize: 13,
          fontWeight: 650,
          lineHeight: 1.45,
        }}
      >
        {objectiveSummary}
      </div>
      <section
        aria-label="Goal 目标详情"
        style={{
          maxHeight: 280,
          overflow: 'auto',
          whiteSpace: 'pre-wrap',
          overflowWrap: 'anywhere',
          border: '1px solid var(--pudding-chat-border)',
          borderRadius: 8,
          padding: '10px 12px',
          background: 'var(--pudding-chat-surface-muted)',
          color: 'var(--pudding-chat-text-secondary)',
          fontSize: 12,
          lineHeight: 1.55,
        }}
      >
        {goal.objective}
      </section>

      {(goal.statusReason || (terminal && goal.terminalAtUtc)) && (
        <div
          style={{
            display: 'flex',
            flexWrap: 'wrap',
            gap: '4px 12px',
            marginTop: 10,
            color: 'var(--pudding-chat-text-subtle)',
            fontSize: 12,
          }}
        >
          {goal.statusReason && <span>原因：{goal.statusReason}</span>}
          {terminal && goal.terminalAtUtc && (
            <span>终止于 {new Date(goal.terminalAtUtc).toLocaleString()}</span>
          )}
        </div>
      )}

      {!terminal && (
        <>
          <Divider style={{ margin: '12px 0 10px' }} />
          <Space size={8} wrap>
            {goal.phase === 'active' ? (
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
            ) : (
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
            <Popconfirm
              title="停止这个 Goal？"
              description="停止会写入可审计的取消终态；已产生的 Iteration、事件与证据会保留。"
              okText="停止"
              cancelText="返回"
              okButtonProps={{ danger: true }}
              onConfirm={() => void run('cancel', 'user_stop_from_banner')}
            >
              <Tooltip title="停止 Goal；不删除已经产生的证据">
                <Button
                  size="small"
                  danger
                  icon={<StopOutlined />}
                  disabled={commandRunning}
                >
                  停止
                </Button>
              </Tooltip>
            </Popconfirm>
          </Space>
        </>
      )}
      {terminal && (
        <>
          <Divider style={{ margin: '12px 0 10px' }} />
          <Button
            size="small"
            type="primary"
            icon={<PlusOutlined />}
            disabled={commandRunning}
            onClick={() => setStartOpen(true)}
          >
            新建 Goal
          </Button>
        </>
      )}
    </div>
  );

  return (
    <>
      {contextHolder}
      <Popover
        placement="bottomLeft"
        trigger={['hover', 'click']}
        title={`Goal ${phaseText} · Iteration ${progress}`}
        content={details}
      >
        <Button
          size="small"
          icon={<ThunderboltOutlined />}
          aria-haspopup="dialog"
          aria-label={`Goal ${phaseText}，Iteration ${progress}，查看详情`}
          data-goal-phase={goal.phase}
          title={objectiveSummary}
          style={{
            height: 28,
            maxWidth: 190,
            color: tone.color,
            background: tone.background,
            borderColor: tone.borderColor,
            borderRadius: 999,
            fontWeight: 600,
            flexShrink: 0,
          }}
        >
          Goal {phaseText} · {progress}
        </Button>
      </Popover>
      {startModal}
    </>
  );
};

export default GoalBanner;
