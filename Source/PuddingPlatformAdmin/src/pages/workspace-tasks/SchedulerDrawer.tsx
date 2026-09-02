import {
  CaretRightOutlined,
  PauseOutlined,
  ReloadOutlined,
  SafetyCertificateOutlined,
  SettingOutlined,
  SyncOutlined,
  ToolOutlined,
} from '@ant-design/icons';
import {
  Alert,
  App,
  Button,
  Card,
  Descriptions,
  Divider,
  Drawer,
  Empty,
  Form,
  InputNumber,
  Radio,
  Space,
  Spin,
  Statistic,
  Switch,
  Table,
  Tag,
  Typography,
} from 'antd';
import type { ColumnsType } from 'antd/es/table';
import dayjs from 'dayjs';
import React, { useCallback, useEffect, useMemo, useState } from 'react';
import {
  evaluateTaskAutoDispatch,
  executeTaskSchedulerAction,
  getTaskSchedulerStatus,
  updateTaskSchedulerPolicy,
} from '@/services/platform/api';
import type {
  TaskAutoDispatchCandidateDecisionDto,
  TaskSchedulerPolicyUpdateRequest,
  TaskSchedulerStatusDto,
} from './types';

const { Text, Title } = Typography;

interface SchedulerPolicyFormValues {
  enabled: boolean;
  mode: 'shadow' | 'authoritative';
  scanIntervalSeconds: number;
  candidateLimit: number;
  maxStartsPerScan: number;
  eventDrivenEnabled: boolean;
}

export interface SchedulerDrawerProps {
  open: boolean;
  workspaceId: string;
  onClose: () => void;
  onReconciled: () => void;
}

const STATE_PRESENTATION: Record<
  TaskSchedulerStatusDto['state'],
  { text: string; color: string }
> = {
  disabled: { text: '未启用', color: 'default' },
  paused: { text: '已暂停', color: 'orange' },
  shadow: { text: '影子评估', color: 'blue' },
  authoritative: { text: '自动调度中', color: 'green' },
  scanning: { text: '扫描中', color: 'processing' },
  faulted: { text: '异常', color: 'red' },
};

function formatTime(value?: string): string {
  if (!value) return '—';
  const parsed = dayjs(value);
  return parsed.isValid() ? parsed.format('MM-DD HH:mm:ss') : value;
}

function errorMessage(error: unknown): string {
  const candidate = error as {
    response?: { data?: { message?: unknown } };
    message?: unknown;
  };
  const apiMessage = candidate.response?.data?.message;
  if (typeof apiMessage === 'string') return apiMessage;
  return typeof candidate.message === 'string' ? candidate.message : '调度操作失败';
}

function verdictLabel(value: TaskAutoDispatchCandidateDecisionDto['verdict']) {
  if (value === 'Eligible' || value === 0) return <Tag color="green">可派发</Tag>;
  if (value === 'Deferred' || value === 1) return <Tag color="orange">等待</Tag>;
  return <Tag color="red">拒绝</Tag>;
}

export const SchedulerDrawer: React.FC<SchedulerDrawerProps> = ({
  open,
  workspaceId,
  onClose,
  onReconciled,
}) => {
  const { message } = App.useApp();
  const [form] = Form.useForm<SchedulerPolicyFormValues>();
  const [status, setStatus] = useState<TaskSchedulerStatusDto | null>(null);
  const [decisions, setDecisions] = useState<TaskAutoDispatchCandidateDecisionDto[]>([]);
  const [loading, setLoading] = useState(false);
  const [runningAction, setRunningAction] = useState<string | null>(null);

  const load = useCallback(async () => {
    if (!open) return;
    setLoading(true);
    try {
      const nextStatus = await getTaskSchedulerStatus(workspaceId);
      const nextDecisions = await evaluateTaskAutoDispatch(
        workspaceId,
        nextStatus.policy.candidateLimit,
      );
      setStatus(nextStatus);
      setDecisions(nextDecisions);
      form.setFieldsValue({
        enabled: nextStatus.policy.enabled,
        mode: nextStatus.policy.mode,
        scanIntervalSeconds: nextStatus.policy.scanIntervalSeconds,
        candidateLimit: nextStatus.policy.candidateLimit,
        maxStartsPerScan: nextStatus.policy.maxStartsPerScan,
        eventDrivenEnabled: nextStatus.policy.eventDrivenEnabled,
      });
    } catch (error) {
      message.error(errorMessage(error));
    } finally {
      setLoading(false);
    }
  }, [form, message, open, workspaceId]);

  useEffect(() => {
    void load();
  }, [load]);

  const runAction = async (action: 'pause' | 'resume' | 'scan' | 'repair') => {
    if (!status) return;
    setRunningAction(action);
    try {
      if (action === 'pause' || action === 'resume') {
        const next = await executeTaskSchedulerAction(
          workspaceId,
          action,
          status.policy.revision,
        );
        setStatus(next);
        message.success(action === 'pause' ? '自动调度已暂停' : '自动调度已恢复');
      } else {
        const result = await executeTaskSchedulerAction(workspaceId, action);
        setStatus(result.status);
        message.success(
          action === 'scan'
            ? `扫描完成：启动 ${result.summary.started}，修复 ${result.summary.repaired}`
            : `修复完成：处理 ${result.summary.repaired} 项`,
        );
        onReconciled();
      }
      await load();
    } catch (error) {
      message.error(errorMessage(error));
      await load();
    } finally {
      setRunningAction(null);
    }
  };

  const savePolicy = async () => {
    if (!status) return;
    let values: SchedulerPolicyFormValues;
    try {
      values = await form.validateFields();
    } catch {
      return;
    }
    setRunningAction('save');
    try {
      const body: TaskSchedulerPolicyUpdateRequest = {
        expectedRevision: status.policy.revision,
        paused: status.policy.paused,
        ...values,
      };
      const next = await updateTaskSchedulerPolicy(workspaceId, body);
      setStatus(next);
      message.success('调度策略已保存并热加载');
      await load();
    } catch (error) {
      message.error(errorMessage(error));
      await load();
    } finally {
      setRunningAction(null);
    }
  };

  const columns = useMemo<ColumnsType<TaskAutoDispatchCandidateDecisionDto>>(
    () => [
      {
        title: '任务',
        dataIndex: 'taskId',
        ellipsis: true,
        render: (value: string, row) => (
          <div style={{ maxWidth: 180 }}>
            <Text ellipsis={{ tooltip: value }}>{value}</Text>
            {row.taskType && (
              <div><Text type="secondary" style={{ fontSize: 11 }}>{row.taskType}</Text></div>
            )}
          </div>
        ),
      },
      { title: '判定', dataIndex: 'verdict', width: 82, render: verdictLabel },
      {
        title: '原因码',
        dataIndex: 'code',
        ellipsis: true,
        render: (value: string) => <Text code>{value}</Text>,
      },
      {
        title: 'Agent / 下一时间',
        width: 180,
        render: (_, row) => (
          <div>
            <Text>{row.agentId ?? '—'}</Text>
            <div><Text type="secondary" style={{ fontSize: 11 }}>{formatTime(row.nextEligibleAtUtc)}</Text></div>
          </div>
        ),
      },
    ],
    [],
  );

  const last = status?.lastScan;
  const presentation = status ? STATE_PRESENTATION[status.state] : null;

  return (
    <Drawer
      title={
        <Space>
          <SettingOutlined />
          调度中心
          {presentation && <Tag color={presentation.color}>{presentation.text}</Tag>}
        </Space>
      }
      open={open}
      onClose={onClose}
      width={760}
      extra={<Button icon={<ReloadOutlined />} onClick={() => void load()}>刷新</Button>}
    >
      <Spin spinning={loading && !status}>
        {!status ? (
          <Empty description="调度状态不可用" />
        ) : (
          <>
            {status.lastError && (
              <Alert
                type="error"
                showIcon
                message="最近一次调度失败"
                description={status.lastError}
                style={{ marginBottom: 12 }}
              />
            )}
            {!status.policy.enabled && (
              <Alert
                type="info"
                showIcon
                message="自动调度未启用"
                description="任务看板仍可手工管理；启用后，只有勾选“自动调度”的任务才会进入候选集。"
                style={{ marginBottom: 12 }}
              />
            )}
            {status.policy.paused && (
              <Alert
                type="warning"
                showIcon
                message="当前工作区已暂停新的自动准入"
                description="进行中的 Goal 不会被强杀；请在 Chat Header 的 Goal 控件中单独暂停或停止。立即扫描仍可由用户显式执行。"
                style={{ marginBottom: 12 }}
              />
            )}

            <Space wrap style={{ marginBottom: 12 }}>
              {status.policy.paused ? (
                <Button
                  type="primary"
                  icon={<CaretRightOutlined />}
                  disabled={!status.policy.enabled}
                  loading={runningAction === 'resume'}
                  onClick={() => void runAction('resume')}
                >
                  恢复自动调度
                </Button>
              ) : (
                <Button
                  icon={<PauseOutlined />}
                  disabled={!status.policy.enabled}
                  loading={runningAction === 'pause'}
                  onClick={() => void runAction('pause')}
                >
                  暂停自动调度
                </Button>
              )}
              <Button
                icon={<SyncOutlined />}
                disabled={!status.policy.enabled}
                loading={runningAction === 'scan'}
                onClick={() => void runAction('scan')}
              >
                立即扫描
              </Button>
              <Button
                icon={<ToolOutlined />}
                loading={runningAction === 'repair'}
                onClick={() => void runAction('repair')}
              >
                立即修复
              </Button>
            </Space>

            <div
              style={{
                display: 'grid',
                gridTemplateColumns: 'repeat(4, minmax(0, 1fr))',
                gap: 8,
              }}
            >
              <Card size="small"><Statistic title="Idle Agent" value={last?.idleAgents ?? 0} /></Card>
              <Card size="small"><Statistic title="候选 / 可派发" value={`${last?.candidates ?? decisions.length} / ${last?.eligible ?? decisions.filter((item) => item.verdict === 'Eligible' || item.verdict === 0).length}`} /></Card>
              <Card size="small"><Statistic title="本轮启动" value={last?.started ?? 0} /></Card>
              <Card size="small"><Statistic title="本轮修复" value={last?.repaired ?? 0} /></Card>
            </div>

            <Descriptions size="small" column={2} style={{ marginTop: 12 }}>
              <Descriptions.Item label="最近扫描">{formatTime(last?.completedAtUtc)}</Descriptions.Item>
              <Descriptions.Item label="预计下次">{formatTime(status.nextScanEstimateUtc)}</Descriptions.Item>
              <Descriptions.Item label="Busy / Unknown">{last?.busyAgents ?? 0} / {last?.unknownAgents ?? 0}</Descriptions.Item>
              <Descriptions.Item label="Tracked / Cleanup">{last?.tracked ?? 0} / {last?.cleanupRequired ?? 0}</Descriptions.Item>
            </Descriptions>

            <Divider />
            <Title level={5} style={{ marginTop: 0 }}>候选决策</Title>
            {decisions.length === 0 ? (
              <Alert
                type="warning"
                showIcon
                message="当前没有自动派发候选"
                description="最常见原因是 Task 未勾选“自动调度”，或仍未进入 Ready/Deferred；请从任务卡菜单选择“纳入自动调度”，再检查任务类型、能力、Agent 和执行窗口。"
              />
            ) : (
              <Table
                size="small"
                rowKey="taskId"
                columns={columns}
                dataSource={decisions}
                pagination={{ pageSize: 8, hideOnSinglePage: true }}
                scroll={{ x: 620 }}
              />
            )}

            <Divider />
            <Space align="center" style={{ marginBottom: 8 }}>
              <SafetyCertificateOutlined />
              <Title level={5} style={{ margin: 0 }}>调度策略</Title>
              <Text type="secondary">revision {status.policy.revision}</Text>
            </Space>
            <Form form={form} layout="vertical">
              <Space size={24} wrap align="start">
                <Form.Item name="enabled" label="自动调度" valuePropName="checked">
                  <Switch checkedChildren="启用" unCheckedChildren="关闭" />
                </Form.Item>
                <Form.Item name="eventDrivenEnabled" label="事件驱动" valuePropName="checked">
                  <Switch checkedChildren="启用" unCheckedChildren="恢复扫描" />
                </Form.Item>
                <Form.Item name="mode" label="运行模式">
                  <Radio.Group
                    optionType="button"
                    buttonStyle="solid"
                    options={[
                      { label: 'Shadow', value: 'shadow' },
                      {
                        label: 'Authoritative',
                        value: 'authoritative',
                        disabled: !status.prerequisites.authoritativeReady,
                      },
                    ]}
                  />
                </Form.Item>
              </Space>
              <Space size={12} wrap align="start">
                <Form.Item
                  name="scanIntervalSeconds"
                  label="恢复扫描周期（秒）"
                  rules={[{ type: 'number', min: 1, max: 3600 }]}
                >
                  <InputNumber min={1} max={3600} style={{ width: 160 }} />
                </Form.Item>
                <Form.Item
                  name="candidateLimit"
                  label="候选上限"
                  rules={[{ type: 'number', min: 1, max: 500 }]}
                >
                  <InputNumber min={1} max={500} style={{ width: 140 }} />
                </Form.Item>
                <Form.Item
                  name="maxStartsPerScan"
                  label="每轮最多启动"
                  rules={[{ type: 'number', min: 1, max: 32 }]}
                >
                  <InputNumber min={1} max={32} style={{ width: 140 }} />
                </Form.Item>
              </Space>
              <Alert
                type={status.prerequisites.authoritativeReady ? 'success' : 'warning'}
                showIcon
                message={status.prerequisites.authoritativeReady ? 'Authoritative 前置门禁已满足' : 'Authoritative 前置门禁未满足'}
                description={`TaskBoundGoals=${status.prerequisites.taskBoundGoalsEnabled} · GoalRuns=${status.prerequisites.goalRunsEnabled} · Continuation=${status.prerequisites.goalContinuationEnabled} · Idle grace=${status.policy.minimumIdleSeconds}s · Stall=${status.policy.trackerStallSeconds}s`}
                style={{ marginBottom: 12 }}
              />
              <Button
                type="primary"
                loading={runningAction === 'save'}
                onClick={() => void savePolicy()}
              >
                保存并热加载
              </Button>
            </Form>
          </>
        )}
      </Spin>
    </Drawer>
  );
};

export default SchedulerDrawer;
