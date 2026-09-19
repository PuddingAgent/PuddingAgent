// ── GoalBanner：ADR-074 Goal 顶部状态入口 ─────────────────────────────
// 默认只占用 Header 中一个紧凑按钮；完整 objective / reason / controls
// 仅在 hover 或 click 后的 Popover 中展示，避免长 Task Goal 挤占会话空间。

import {
  CaretRightOutlined,
  PauseOutlined,
  PlusOutlined,
  RiseOutlined,
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
// GoalStepsPanel 只在详情 Popover 内出现（不在首屏必经链），故惰性化：把它的体积
// 移出 Chat 路由首屏 chunk（实测回收 11,710 B，见 Docs/Reports/Chat-Bundle-Budget-Plan-2026-09-19.md）。
// 测试环境必须同步 require：否则 Jest 会因 Suspense 异步挂载而出现断言抖动
// （沿用 components/ChatMain.tsx 既有写法）。
const loadGoalStepsPanel = () => import('./GoalStepsPanel');
const GoalStepsPanel =
  process.env.NODE_ENV === 'test'
    ? (require('./GoalStepsPanel')
        .default as typeof import('./GoalStepsPanel').default)
    : React.lazy(loadGoalStepsPanel);
import {
  describeGoalBlocker,
  isKnownGoalBlockerCode,
} from './goalBlockerCodes';

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

interface GoalExtendValues {
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
    color: 'var(--pudding-chat-text-subtle)',
    background: 'var(--pudding-chat-surface-muted)',
    borderColor: 'var(--pudding-chat-border)',
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
    color: 'var(--pudding-chat-text-subtle)',
    background: 'var(--pudding-chat-surface-muted)',
    borderColor: 'var(--pudding-chat-border)',
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
  const [extendOpen, setExtendOpen] = useState(false);
  const [extendForm] = Form.useForm<GoalExtendValues>();

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

  const extendGoal = async () => {
    let values: GoalExtendValues;
    try {
      values = await extendForm.validateFields();
    } catch {
      return;
    }
    const text = await onCommand('extend', { rounds: values.rounds });
    void messageApi.info(text);
    setExtendOpen(false);
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

  const extendModal = (
    <Modal
      title="延长额度"
      open={extendOpen}
      okText="延长"
      cancelText="取消"
      confirmLoading={commandRunning}
      onOk={() => void extendGoal()}
      onCancel={() => setExtendOpen(false)}
      destroyOnHidden
    >
      <Form
        form={extendForm}
        layout="vertical"
        initialValues={{ rounds: 3 }}
        preserve={false}
      >
        <Form.Item
          name="rounds"
          label="追加 Iteration 数"
          rules={[{ required: true, message: '请输入追加的 Iteration 数' }]}
          extra="仅额度耗尽的 Goal 可延长；已消费额度不会重置。若服务端尚未支持该动作，会明确提示失败。"
        >
          <InputNumber
            min={1}
            max={256}
            precision={0}
            autoFocus
            style={{ width: 160 }}
          />
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
        {extendModal}
      </>
    );
  }

  const knownPhase = Object.prototype.hasOwnProperty.call(PHASE_TONE, goal.phase);
  const terminal = isTerminalGoalPhase(goal.phase);
  const progress = `${goal.iterationsStarted}/${goal.maxIterations}`;
  const phaseText = knownPhase ? PHASE_TEXT[goal.phase] : `未知状态（${goal.phase}）`;
  const objectiveSummary = firstObjectiveLine(goal.objective);
  // 详情区只展示 objective 的非首行；首行已作为上方标题，避免同屏重复。
  const objectiveRest = goal.objective
    .split(/\r?\n/)
    .map((line) => line.trim())
    .filter(Boolean)
    .slice(1)
    .join('\n');
  const blockedCode = (goal.blockedCode ?? '').trim();
  const blocker = describeGoalBlocker(blockedCode);
  const tone = knownPhase ? PHASE_TONE[goal.phase] : PHASE_TONE.failed;

  const run = async (
    action: 'pause' | 'resume' | 'cancel' | 'clear',
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
        {objectiveRest || '目标仅一行，完整内容即上方标题。'}
      </section>

      <div
        style={{
          display: 'flex',
          flexWrap: 'wrap',
          gap: '4px 12px',
          marginTop: 8,
          color: 'var(--pudding-chat-text-subtle)',
          fontSize: 12,
          lineHeight: 1.5,
        }}
      >
        <span>目标版本：v{goal.objectiveVersion}</span>
        <span>已结算 Iteration：{goal.iterationsSettled}</span>
        <span>激活纪元：#{goal.activationEpoch}</span>
        {goal.lastNextAction ? (
          <span>下一步动作：{goal.lastNextAction}</span>
        ) : null}
      </div>

      <React.Suspense
        fallback={
          <div
            style={{
              marginTop: 10,
              color: 'var(--pudding-chat-text-subtle)',
              fontSize: 12,
            }}
          >
            正在加载步骤…
          </div>
        }
      >
        <GoalStepsPanel goal={goal} />
      </React.Suspense>

      {((goal.statusReason && !blocker) ||
        (terminal && goal.terminalAtUtc)) && (
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
          {goal.statusReason && !blocker && (
            <span>原因：{goal.statusReason}</span>
          )}
          {terminal && goal.terminalAtUtc && (
            <span>终止于 {new Date(goal.terminalAtUtc).toLocaleString()}</span>
          )}
        </div>
      )}
      {blocker && (
        <div
          role="alert"
          data-goal-blocker-code={blockedCode}
          style={{
            marginTop: 10,
            padding: '8px 10px',
            borderRadius: 8,
            border: '1px solid rgba(250, 140, 22, 0.40)',
            background: 'rgba(250, 140, 22, 0.12)',
            color: '#d46b08',
            fontSize: 12,
            lineHeight: 1.6,
          }}
        >
          <div style={{ fontWeight: 650 }}>【受阻原因】{blocker.title}</div>
          <div>建议动作：{blocker.action}</div>
          <div>
            是否需要你决策：
            {blocker.needsUser || goal.phase === 'blocked' ? (
              <span style={{ fontWeight: 700 }}>需要</span>
            ) : (
              '否'
            )}
          </div>
          <div style={{ color: 'var(--pudding-chat-text-subtle)' }}>
            {isKnownGoalBlockerCode(blockedCode)
              ? `受阻码：${blockedCode}`
              : `未知受阻码：${blockedCode}`}
          </div>
        </div>
      )}

      {knownPhase && !terminal && (
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
          <Space size={8} wrap>
            {goal.phase === 'budget_exhausted' && (
              <Tooltip title="追加 Iteration 上限，使 Goal 可继续推进（仅额度耗尽时有效）">
                <Button
                  size="small"
                  icon={<RiseOutlined />}
                  disabled={commandRunning}
                  onClick={() => setExtendOpen(true)}
                >
                  延长额度
                </Button>
              </Tooltip>
            )}
            <Button
              size="small"
              type="primary"
              icon={<PlusOutlined />}
              disabled={commandRunning}
              onClick={() => setStartOpen(true)}
            >
              新建 Goal
            </Button>
            <Button
              size="small"
              disabled={commandRunning}
              onClick={() => void run('clear')}
            >
              清除记录
            </Button>
          </Space>
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
      {extendModal}
    </>
  );
};

export default GoalBanner;
