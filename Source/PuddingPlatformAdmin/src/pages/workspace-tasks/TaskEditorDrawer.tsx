import {
  App,
  AutoComplete,
  Button,
  DatePicker,
  Drawer,
  Form,
  Input,
  InputNumber,
  Modal,
  Select,
  Space,
  Switch,
  Typography,
} from 'antd';
import React, { useEffect, useState } from 'react';
import dayjs, { type Dayjs } from 'dayjs';
import {
  createTask,
  getTask,
  updateTask,
  type WorkspaceAgentDto,
} from '@/services/platform/api';
import {
  parseTaskError,
  TASK_EXECUTION_WINDOW_WIRES,
  TASK_EXECUTION_WINDOW_LABELS,
  TASK_PRIORITY_WIRES,
  TASK_PRIORITY_LABELS,
  type CreateTaskRequest,
  type PatchTaskRequest,
  type TaskDto,
  type TaskExecutionWindowWire,
  type TaskPriorityWire,
} from './types';

const { TextArea } = Input;
const { Text } = Typography;

interface TaskFormValues {
  title: string;
  description?: string;
  acceptanceCriteria?: string;
  priority: TaskPriorityWire;
  executionWindow: TaskExecutionWindowWire;
  preferredAgentId?: string;
  taskType: string;
  requiredCapabilityIds?: string[];
  requiredProviderId?: string;
  requiredModelId?: string;
  allowAgentFallback: boolean;
  autoDispatchEnabled: boolean;
  notBeforeUtc?: Dayjs;
  dueAtUtc?: Dayjs;
  sortOrder?: number;
}

export interface TaskEditorDrawerProps {
  open: boolean;
  workspaceId: string;
  /** null = 新建模式；否则编辑模式 */
  task: TaskDto | null;
  agents: WorkspaceAgentDto[];
  onClose: () => void;
  onSaved: (task: TaskDto) => void;
}

interface ConflictState {
  actualVersion: number;
}

/** 新建/编辑表单；PATCH CAS；409 冲突保留草稿（ST-08A.3）。 */
export const TaskEditorDrawer: React.FC<TaskEditorDrawerProps> = ({
  open,
  workspaceId,
  task,
  agents,
  onClose,
  onSaved,
}) => {
  const { message } = App.useApp();
  const [form] = Form.useForm<TaskFormValues>();
  const [saving, setSaving] = useState(false);
  const [conflict, setConflict] = useState<ConflictState | null>(null);
  const [baselineVersion, setBaselineVersion] = useState<number>(0);

  const isEdit = task !== null;

  useEffect(() => {
    if (!open) return;
    if (task) {
      setBaselineVersion(task.version);
      form.setFieldsValue({
        title: task.title,
        description: task.description,
        acceptanceCriteria: task.acceptanceCriteria,
        priority: task.priority,
        executionWindow: task.executionWindow,
        preferredAgentId: task.preferredAgentId,
        taskType: task.taskType,
        requiredCapabilityIds: task.requiredCapabilityIds,
        requiredProviderId: task.requiredProviderId,
        requiredModelId: task.requiredModelId,
        allowAgentFallback: task.allowAgentFallback,
        autoDispatchEnabled: task.autoDispatchEnabled,
        notBeforeUtc: task.notBeforeUtc ? dayjs(task.notBeforeUtc) : undefined,
        dueAtUtc: task.dueAtUtc ? dayjs(task.dueAtUtc) : undefined,
        sortOrder: task.sortOrder,
      });
    } else {
      setBaselineVersion(0);
      form.setFieldsValue({
        title: '',
        description: undefined,
        acceptanceCriteria: undefined,
        priority: 'p3',
        executionWindow: 'inherit',
        preferredAgentId: undefined,
        taskType: 'general',
        requiredCapabilityIds: [],
        requiredProviderId: undefined,
        requiredModelId: undefined,
        allowAgentFallback: true,
        autoDispatchEnabled: false,
        sortOrder: 0,
      });
    }
    setConflict(null);
  }, [open, task, form]);

  const buildBody = (values: TaskFormValues) => {
    const base = {
      title: values.title,
      description: values.description?.trim() || undefined,
      acceptanceCriteria: values.acceptanceCriteria?.trim() || undefined,
      priority: values.priority,
      executionWindow: values.executionWindow,
      preferredAgentId: values.preferredAgentId || undefined,
      taskType: values.taskType?.trim().toLowerCase() || 'general',
      requiredCapabilityIds: Array.from(
        new Set(
          (values.requiredCapabilityIds ?? [])
            .map((value) => value.trim())
            .filter(Boolean),
        ),
      ),
      requiredProviderId: values.requiredProviderId?.trim() || undefined,
      requiredModelId: values.requiredModelId?.trim() || undefined,
      allowAgentFallback: values.allowAgentFallback,
      autoDispatchEnabled: values.autoDispatchEnabled,
      notBeforeUtc: values.notBeforeUtc?.toISOString(),
      dueAtUtc: values.dueAtUtc?.toISOString(),
      sortOrder: values.sortOrder ?? 0,
    };
    return base;
  };

  const doSave = async (values: TaskFormValues, expectedVersion: number) => {
    if (isEdit && task) {
      const body: PatchTaskRequest = {
        expectedVersion,
        ...buildBody(values),
      };
      return await updateTask(workspaceId, task.taskId, body);
    }
    const body: CreateTaskRequest = buildBody(values);
    return await createTask(workspaceId, body);
  };

  const handleSave = async () => {
    let values: TaskFormValues;
    try {
      values = await form.validateFields();
    } catch {
      return;
    }
    setSaving(true);
    try {
      const saved = await doSave(values, baselineVersion);
      message.success(isEdit ? '已保存' : '已创建');
      onSaved(saved);
      onClose();
    } catch (error) {
      const parsed = parseTaskError(error);
      if (parsed.body?.code === 'task.version_conflict') {
        const actualVersion =
          parsed.body.actualVersion ?? parsed.body.version ?? 0;
        setConflict({ actualVersion });
      } else {
        message.error(parsed.body?.message ?? '保存失败');
      }
    } finally {
      setSaving(false);
    }
  };

  const handleConflictUseServer = async () => {
    if (!task) return;
    try {
      const serverTask = await getTask(workspaceId, task.taskId);
      setBaselineVersion(serverTask.version);
      form.setFieldsValue({
        title: serverTask.title,
        description: serverTask.description,
        acceptanceCriteria: serverTask.acceptanceCriteria,
        priority: serverTask.priority,
        executionWindow: serverTask.executionWindow,
        preferredAgentId: serverTask.preferredAgentId,
        taskType: serverTask.taskType,
        requiredCapabilityIds: serverTask.requiredCapabilityIds,
        requiredProviderId: serverTask.requiredProviderId,
        requiredModelId: serverTask.requiredModelId,
        allowAgentFallback: serverTask.allowAgentFallback,
        autoDispatchEnabled: serverTask.autoDispatchEnabled,
        notBeforeUtc: serverTask.notBeforeUtc
          ? dayjs(serverTask.notBeforeUtc)
          : undefined,
        dueAtUtc: serverTask.dueAtUtc ? dayjs(serverTask.dueAtUtc) : undefined,
        sortOrder: serverTask.sortOrder,
      });
      setConflict(null);
      message.info('已加载服务端最新版本，请检查后重新保存');
    } catch (error) {
      message.error(parseTaskError(error).body?.message ?? '加载失败');
    }
  };

  const handleConflictRetryWithMine = async () => {
    if (!conflict) return;
    let values: TaskFormValues;
    try {
      values = await form.validateFields();
    } catch {
      return;
    }
    setSaving(true);
    try {
      const saved = await doSave(values, conflict.actualVersion);
      message.success('已保存');
      onSaved(saved);
      setConflict(null);
      onClose();
    } catch (error) {
      const parsed = parseTaskError(error);
      if (parsed.body?.code === 'task.version_conflict') {
        setConflict({
          actualVersion: parsed.body.actualVersion ?? parsed.body.version ?? 0,
        });
      } else {
        message.error(parsed.body?.message ?? '保存失败');
      }
    } finally {
      setSaving(false);
    }
  };

  const priorityOptions = TASK_PRIORITY_WIRES.map((value) => ({
    value,
    label: TASK_PRIORITY_LABELS[value],
  }));
  const windowOptions = TASK_EXECUTION_WINDOW_WIRES.map((value) => ({
    value,
    label: TASK_EXECUTION_WINDOW_LABELS[value],
  }));
  const agentOptions = agents.map((agent) => ({
    value: agent.agentId,
    label: agent.displayName || agent.name || agent.agentId,
  }));

  return (
    <>
      <Drawer
        title={isEdit ? '编辑任务' : '新建任务'}
        open={open}
        onClose={onClose}
        width={520}
        extra={
          <Space>
            <Button onClick={onClose}>取消</Button>
            <Button type="primary" loading={saving} onClick={handleSave}>
              保存
            </Button>
          </Space>
        }
      >
        <Form
          form={form}
          layout="vertical"
          initialValues={{
            priority: 'p3',
            executionWindow: 'inherit',
            taskType: 'general',
            allowAgentFallback: true,
            autoDispatchEnabled: false,
            sortOrder: 0,
          }}
        >
          <Form.Item
            name="title"
            label="标题"
            rules={[{ required: true, message: '请输入标题' }]}
          >
            <Input placeholder="任务标题" />
          </Form.Item>
          <Form.Item name="description" label="描述">
            <TextArea rows={3} placeholder="任务描述" />
          </Form.Item>
          <Form.Item name="acceptanceCriteria" label="验收标准">
            <TextArea rows={3} placeholder="验收标准（Acceptance Criteria）" />
          </Form.Item>
          <Space size={12} style={{ display: 'flex' }} align="start">
            <Form.Item name="priority" label="优先级" style={{ width: 140 }}>
              <Select options={priorityOptions} />
            </Form.Item>
            <Form.Item name="executionWindow" label="执行窗口" style={{ width: 160 }}>
              <Select options={windowOptions} />
            </Form.Item>
          </Space>
          <Form.Item name="preferredAgentId" label="偏好 Agent">
            <Select
              allowClear
              showSearch
              optionFilterProp="label"
              options={agentOptions}
              placeholder="可选"
            />
          </Form.Item>
          <Form.Item
            name="autoDispatchEnabled"
            label="自动调度"
            valuePropName="checked"
            extra="开启后，该任务才会进入后台调度候选集；仍需满足任务状态、Agent 空闲、能力、依赖和执行窗口围栏。"
          >
            <Switch checkedChildren="已纳入" unCheckedChildren="未纳入" />
          </Form.Item>
          <Space size={12} style={{ display: 'flex' }} align="start">
            <Form.Item
              name="taskType"
              label="任务类型"
              style={{ width: 220 }}
              rules={[
                { required: true, message: '请输入任务类型' },
                { max: 64, message: '任务类型最多 64 个字符' },
              ]}
            >
              <AutoComplete
                options={[
                  { value: 'general', label: '通用 general' },
                  { value: 'implementation', label: '开发 implementation' },
                  { value: 'test', label: '测试 test' },
                  { value: 'review', label: '评审 review' },
                  { value: 'research', label: '研究 research' },
                  { value: 'documentation', label: '文档 documentation' },
                  { value: 'deployment', label: '部署 deployment' },
                  { value: 'operations', label: '运维 operations' },
                ]}
                placeholder="general 或自定义类型"
              />
            </Form.Item>
            <Form.Item
              name="allowAgentFallback"
              label="兼容 Agent 回退"
              valuePropName="checked"
            >
              <Switch checkedChildren="允许" unCheckedChildren="禁止" />
            </Form.Item>
          </Space>
          <Form.Item
            name="requiredCapabilityIds"
            label="所需能力"
            extra="输入 Capability ID 后回车；调度器只选择满足全部能力的 Agent。"
          >
            <Select
              mode="tags"
              tokenSeparators={[',', ' ']}
              placeholder="例如 cap-file-write、cap-shell"
            />
          </Form.Item>
          <Space size={12} style={{ display: 'flex' }} align="start">
            <Form.Item
              name="requiredProviderId"
              label="限定 Provider"
              style={{ flex: 1 }}
            >
              <Input placeholder="可选" />
            </Form.Item>
            <Form.Item
              name="requiredModelId"
              label="限定 Model"
              style={{ flex: 1 }}
            >
              <Input placeholder="可选" />
            </Form.Item>
          </Space>
          <Text type="secondary" style={{ display: 'block', marginTop: -8, marginBottom: 16, fontSize: 12 }}>
            自动调度不会提升 Agent 权限；Provider/Model 限制与任务类型路由同时生效。
          </Text>
          <Space size={12} style={{ display: 'flex' }} align="start">
            <Form.Item name="notBeforeUtc" label="最早可执行">
              <DatePicker showTime />
            </Form.Item>
            <Form.Item name="dueAtUtc" label="截止时间">
              <DatePicker showTime />
            </Form.Item>
          </Space>
          <Form.Item name="sortOrder" label="排序序号（小值在前）">
            <InputNumber style={{ width: 140 }} />
          </Form.Item>
        </Form>
      </Drawer>

      <Modal
        title="版本冲突"
        open={conflict !== null}
        onCancel={() => setConflict(null)}
        footer={[
          <Button key="keep" onClick={() => setConflict(null)}>
            保留我的草稿
          </Button>,
          <Button key="server" onClick={handleConflictUseServer}>
            加载服务端版本
          </Button>,
          <Button
            key="retry"
            type="primary"
            onClick={handleConflictRetryWithMine}
          >
            以服务端版本为基底重试
          </Button>,
        ]}
      >
        <p>
          该任务已被其他操作更新（当前版本 {conflict?.actualVersion ?? '?'}）。你的草稿已保留，请选择处理方式。
        </p>
      </Modal>
    </>
  );
};

export default TaskEditorDrawer;
