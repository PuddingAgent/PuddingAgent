import {
  PlusOutlined,
  ReloadOutlined,
} from '@ant-design/icons';
import { PageContainer } from '@ant-design/pro-components';
import {
  App,
  Button,
  Input,
  Modal,
  Radio,
  Segmented,
  Select,
  Space,
  Spin,
  Typography,
} from 'antd';
import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  archiveTask,
  assignTask,
  cancelTask,
  getTask,
  getWorkspace,
  listTasks,
  listWorkspaceAgents,
  markFailedTask,
  reopenTask,
  requeueTask,
  resumeTask,
  runNowTask,
  type WorkspaceAgentDto,
  type WorkspaceWithPermDto,
} from '@/services/platform/api';
import { watchTasks } from './api';
import { TaskBoard } from './TaskBoard';
import type { TaskActions } from './TaskCard';
import { TaskDetailsDrawer } from './TaskDetailsDrawer';
import { TaskEditorDrawer } from './TaskEditorDrawer';
import { TaskTable } from './TaskTable';
import {
  BOARD_COLUMN_ORDER,
  BOARD_COLUMN_WIRES,
  emptyColumns,
  parseTaskError,
  removeTaskFromColumns,
  upsertTaskIntoColumns,
  type BoardColumnWire,
  type CommandTaskRequest,
  type TaskColumns,
  type TaskCommandWire,
  type TaskDto,
  type TaskEventWatchEvent,
  type TaskPriorityWire,
  type WindowDecisionWire,
} from './types';

const { Text } = Typography;

const WATCH_CURSOR_PREFIX = 'pudding:taskWatchCursor:';
const MAX_EVENTS_PER_TASK = 200;

type ViewMode = 'board' | 'table';

interface Filters {
  priority?: TaskPriorityWire;
  agentId?: string;
  search: string;
}

interface AssignmentState {
  task: TaskDto;
  mode: 'assign' | 'runNow';
}

const PRIORITY_OPTIONS = [
  { value: 'p0', label: 'P0' },
  { value: 'p1', label: 'P1' },
  { value: 'p2', label: 'P2' },
  { value: 'p3', label: 'P3' },
];

const COMMAND_LABELS: Record<TaskCommandWire, string> = {
  Create: '创建',
  Update: '更新',
  Assign: '指派',
  RunNow: '执行',
  Cancel: '取消',
  Archive: '归档',
  Reopen: '重新打开',
  MarkFailed: '标记失败',
  Resume: '恢复',
  Requeue: '重新排队',
};

function isBoardColumn(value: unknown): value is BoardColumnWire {
  return (BOARD_COLUMN_WIRES as readonly unknown[]).includes(value);
}

export interface WorkspaceTasksPanelProps {
  workspaceId: string;
}

/** 任务看板主体（可复用组件，workspaceId 由 props 传入，不依赖路由参数）。 */
export function WorkspaceTasksPanel({ workspaceId }: WorkspaceTasksPanelProps) {
  const { message, modal } = App.useApp();

  const [workspace, setWorkspace] = useState<WorkspaceWithPermDto | null>(null);
  const [agents, setAgents] = useState<WorkspaceAgentDto[]>([]);
  const [columns, setColumns] = useState<TaskColumns>(emptyColumns);
  const [viewMode, setViewMode] = useState<ViewMode>('board');
  const [filters, setFilters] = useState<Filters>({ search: '' });
  const [loadingWorkspace, setLoadingWorkspace] = useState(true);

  const [selectedTask, setSelectedTask] = useState<TaskDto | null>(null);
  const [editorOpen, setEditorOpen] = useState(false);
  const [editingTask, setEditingTask] = useState<TaskDto | null>(null);
  const [assignment, setAssignment] = useState<AssignmentState | null>(null);
  const [markFailed, setMarkFailed] = useState<TaskDto | null>(null);
  const [eventsByTask, setEventsByTask] = useState<
    Record<string, TaskEventWatchEvent[]>
  >({});

  const inFlightRef = useRef<Set<string>>(new Set());

  // ─── 初始加载：工作区 + Agent ──────────────────────────────────────────
  useEffect(() => {
    let active = true;
    (async () => {
      try {
        const [ws, agentList] = await Promise.all([
          getWorkspace(workspaceId),
          listWorkspaceAgents(workspaceId),
        ]);
        if (!active) return;
        setWorkspace(ws);
        setAgents(agentList);
      } catch {
        if (active) message.error('加载工作区失败');
      } finally {
        if (active) setLoadingWorkspace(false);
      }
    })();
    return () => {
      active = false;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [workspaceId]);

  // ─── Snapshot：5 列并行 GET list ───────────────────────────────────────
  const loadSnapshot = useCallback(async () => {
    const fresh = emptyColumns();
    setColumns(fresh);
    await Promise.all(
      BOARD_COLUMN_ORDER.map(async (column) => {
        try {
          const page = await listTasks(workspaceId, {
            boardColumn: column,
            priority: filters.priority,
            agentId: filters.agentId,
            limit: 100,
          });
          setColumns((prev) => ({
            ...prev,
            [column]: {
              items: page.items,
              nextCursor: page.nextCursor,
              loading: false,
              loadingMore: false,
              hasMore: page.nextCursor !== null,
            },
          }));
        } catch {
          setColumns((prev) => ({
            ...prev,
            [column]: { ...prev[column], loading: false },
          }));
        }
      }),
    );
  }, [workspaceId, filters.priority, filters.agentId]);

  useEffect(() => {
    if (!workspaceId) return;
    loadSnapshot();
  }, [loadSnapshot, workspaceId]);

  // ─── Watch：SSE Cursor Watch，断线按 Last-Event-ID 追赶 ───────────────
  const reconcileTask = useCallback(
    async (taskId: string) => {
      if (inFlightRef.current.has(taskId)) return;
      inFlightRef.current.add(taskId);
      try {
        const task = await getTask(workspaceId, taskId);
        setColumns((prev) => {
          if (isBoardColumn(task.boardColumn)) {
            return upsertTaskIntoColumns(prev, task);
          }
          return removeTaskFromColumns(prev, taskId);
        });
      } catch (error) {
        const parsed = parseTaskError(error);
        if (parsed.httpStatus === 404) {
          setColumns((prev) => removeTaskFromColumns(prev, taskId));
        }
      } finally {
        inFlightRef.current.delete(taskId);
      }
    },
    [workspaceId],
  );

  useEffect(() => {
    if (!workspaceId) return;
    const controller = new AbortController();
    const cursorKey = `${WATCH_CURSOR_PREFIX}${workspaceId}`;
    const stored = Number(localStorage.getItem(cursorKey) ?? 0) || 0;

    watchTasks({
      workspaceId,
      afterSequence: stored,
      signal: controller.signal,
      onEvent: (event) => {
        localStorage.setItem(cursorKey, String(event.sequence));
        setEventsByTask((prev) => {
          const list = [
            ...(prev[event.taskId] ?? []),
            event,
          ].sort((a, b) => a.sequence - b.sequence);
          return {
            ...prev,
            [event.taskId]: list.slice(-MAX_EVENTS_PER_TASK),
          };
        });
        reconcileTask(event.taskId);
      },
    }).catch(() => {
      /* HTTP 400/401/403/404/409 不重连，静默停止 */
    });

    return () => controller.abort();
  }, [workspaceId, reconcileTask]);

  // ─── 加载更多 ─────────────────────────────────────────────────────────
  const loadMore = useCallback(
    async (column: BoardColumnWire) => {
      const slice = columns[column];
      if (!slice.hasMore || slice.loadingMore || !slice.nextCursor) return;
      setColumns((prev) => ({
        ...prev,
        [column]: { ...prev[column], loadingMore: true },
      }));
      try {
        const page = await listTasks(workspaceId, {
          boardColumn: column,
          priority: filters.priority,
          agentId: filters.agentId,
          limit: 100,
          cursor: slice.nextCursor,
        });
        setColumns((prev) => ({
          ...prev,
          [column]: {
            ...prev[column],
            items: [...prev[column].items, ...page.items],
            nextCursor: page.nextCursor,
            loadingMore: false,
            hasMore: page.nextCursor !== null,
          },
        }));
      } catch {
        setColumns((prev) => ({
          ...prev,
          [column]: { ...prev[column], loadingMore: false },
        }));
      }
    },
    [columns, workspaceId, filters.priority, filters.agentId],
  );

  // ─── 命令执行（通用） ─────────────────────────────────────────────────
  const executeCommand = useCallback(
    async (task: TaskDto, command: TaskCommandWire, reason?: string) => {
      const body: CommandTaskRequest = {
        expectedVersion: task.version,
        reason,
      };
      const result = await (() => {
        switch (command) {
          case 'Cancel':
            return cancelTask(workspaceId, task.taskId, body);
          case 'Archive':
            return archiveTask(workspaceId, task.taskId, body);
          case 'Reopen':
            return reopenTask(workspaceId, task.taskId, body);
          case 'MarkFailed':
            return markFailedTask(workspaceId, task.taskId, body);
          case 'Resume':
            return resumeTask(workspaceId, task.taskId, body);
          case 'Requeue':
            return requeueTask(workspaceId, task.taskId, body);
          default:
            throw new Error(`不支持的命令：${command}`);
        }
      })();
      return result;
    },
    [workspaceId],
  );

  const applyCommandResult = useCallback((result: TaskDto) => {
    setColumns((prev) => {
      if (isBoardColumn(result.boardColumn)) {
        return upsertTaskIntoColumns(prev, result);
      }
      return removeTaskFromColumns(prev, result.taskId);
    });
  }, []);

  const handleCommand = useCallback(
    (task: TaskDto, command: TaskCommandWire) => {
      if (command === 'MarkFailed') {
        setMarkFailed(task);
        return;
      }
      const label = COMMAND_LABELS[command];
      modal.confirm({
        title: `${label}该任务？`,
        content: `任务「${task.title}」将执行「${label}」操作。`,
        okText: label,
        okType: command === 'Cancel' || command === 'Archive' ? 'danger' : 'primary',
        cancelText: '取消',
        onOk: async () => {
          try {
            const result = await executeCommand(task, command);
            applyCommandResult(result);
            message.success(`${label}成功`);
          } catch (error) {
            const parsed = parseTaskError(error);
            if (parsed.body?.code === 'task.version_conflict') {
              message.error('任务已被更新，已刷新最新状态');
              reconcileTask(task.taskId);
            } else {
              message.error(parsed.body?.message ?? `${label}失败`);
            }
          }
        },
      });
    },
    [modal, executeCommand, applyCommandResult, message, reconcileTask],
  );

  // ─── 指派 / 执行 ──────────────────────────────────────────────────────
  const handleAssignmentSubmit = useCallback(
    async (agentId: string, windowDecision?: WindowDecisionWire) => {
      if (!assignment) return;
      const { task, mode } = assignment;
      try {
        const result =
          mode === 'assign'
            ? await assignTask(workspaceId, task.taskId, {
                agentId,
                expectedVersion: task.version,
              })
            : await runNowTask(workspaceId, task.taskId, {
                agentId,
                expectedVersion: task.version,
                windowDecision,
              });
        applyCommandResult(result);
        message.success(mode === 'assign' ? '指派成功' : '已提交执行');
        setAssignment(null);
      } catch (error) {
        const parsed = parseTaskError(error);
        if (parsed.body?.code === 'task.version_conflict') {
          message.error('任务已被更新，已刷新最新状态');
          setAssignment(null);
          reconcileTask(task.taskId);
        } else {
          message.error(parsed.body?.message ?? '操作失败');
        }
      }
    },
    [assignment, workspaceId, applyCommandResult, message, reconcileTask],
  );

  // ─── 编辑/详情回调 ────────────────────────────────────────────────────
  const handleSaved = useCallback(
    (task: TaskDto) => {
      setColumns((prev) => {
        if (isBoardColumn(task.boardColumn)) {
          return upsertTaskIntoColumns(prev, task);
        }
        return removeTaskFromColumns(prev, task.taskId);
      });
    },
    [],
  );

  const handleDeleted = useCallback((taskId: string) => {
    setColumns((prev) => removeTaskFromColumns(prev, taskId));
    setSelectedTask((current) => (current?.taskId === taskId ? null : current));
  }, []);

  const actions: TaskActions = useMemo(
    () => ({
      onOpen: (task) => setSelectedTask(task),
      onEdit: (task) => {
        setEditingTask(task);
        setEditorOpen(true);
      },
      onAssign: (task) => setAssignment({ task, mode: 'assign' }),
      onRunNow: (task) => setAssignment({ task, mode: 'runNow' }),
      onCommand: handleCommand,
    }),
    [handleCommand],
  );

  // ─── 派生：搜索过滤 + 表格数据 ────────────────────────────────────────
  const searchLower = filters.search.trim().toLowerCase();
  const visibleColumns = useMemo<TaskColumns>(() => {
    if (!searchLower) return columns;
    const result = { ...columns };
    for (const column of BOARD_COLUMN_ORDER) {
      result[column] = {
        ...columns[column],
        items: columns[column].items.filter(
          (task) =>
            task.title.toLowerCase().includes(searchLower) ||
            (task.description ?? '').toLowerCase().includes(searchLower),
        ),
      };
    }
    return result;
  }, [columns, searchLower]);

  const allTasks = useMemo<TaskDto[]>(() => {
    const seen = new Set<string>();
    const merged: TaskDto[] = [];
    for (const column of BOARD_COLUMN_ORDER) {
      for (const task of columns[column].items) {
        if (seen.has(task.taskId)) continue;
        seen.add(task.taskId);
        merged.push(task);
      }
    }
    return merged;
  }, [columns]);

  const tableItems = useMemo(() => {
    if (!searchLower) return allTasks;
    return allTasks.filter(
      (task) =>
        task.title.toLowerCase().includes(searchLower) ||
        (task.description ?? '').toLowerCase().includes(searchLower),
    );
  }, [allTasks, searchLower]);

  const agentOptions = useMemo(
    () =>
      agents.map((agent) => ({
        value: agent.agentId,
        label: agent.displayName || agent.name || agent.agentId,
      })),
    [agents],
  );

  if (loadingWorkspace) {
    return (
      <PageContainer>
        <Spin size="large" style={{ display: 'block', margin: '80px auto' }} />
      </PageContainer>
    );
  }

  const selectedEvents = selectedTask
    ? eventsByTask[selectedTask.taskId] ?? []
    : [];

  return (
    <PageContainer
      header={{
        title: (
          <Space>
            {workspace?.name ?? workspaceId}
            <Text type="secondary">任务看板</Text>
          </Space>
        ),
        subTitle: `工作区 ID：${workspaceId}`,
        extra: [
          <Button
            key="refresh"
            icon={<ReloadOutlined />}
            onClick={() => loadSnapshot()}
          >
            刷新
          </Button>,
          <Button
            key="create"
            type="primary"
            icon={<PlusOutlined />}
            onClick={() => {
              setEditingTask(null);
              setEditorOpen(true);
            }}
          >
            新建任务
          </Button>,
        ],
      }}
    >
      <div style={{ marginBottom: 12 }}>
        <Space wrap>
          <Segmented
            value={viewMode}
            onChange={(value) => setViewMode(value as ViewMode)}
            options={[
              { label: '看板', value: 'board' },
              { label: '列表', value: 'table' },
            ]}
          />
          <Select
            allowClear
            placeholder="优先级"
            style={{ width: 120 }}
            options={PRIORITY_OPTIONS}
            value={filters.priority}
            onChange={(value?: TaskPriorityWire) =>
              setFilters((prev) => ({ ...prev, priority: value }))
            }
          />
          <Select
            allowClear
            showSearch
            optionFilterProp="label"
            placeholder="Agent"
            style={{ width: 200 }}
            options={agentOptions}
            value={filters.agentId}
            onChange={(value?: string) =>
              setFilters((prev) => ({ ...prev, agentId: value }))
            }
          />
          <Input.Search
            allowClear
            placeholder="搜索标题/描述"
            style={{ width: 220 }}
            value={filters.search}
            onChange={(e) =>
              setFilters((prev) => ({ ...prev, search: e.target.value }))
            }
          />
        </Space>
      </div>

      <div style={{ height: 'calc(80vh - 230px)', minHeight: 420 }}>
        {viewMode === 'board' ? (
          <TaskBoard
            columns={visibleColumns}
            actions={actions}
            onLoadMore={loadMore}
          />
        ) : (
          <TaskTable items={tableItems} onOpen={setSelectedTask} />
        )}
      </div>

      <TaskEditorDrawer
        open={editorOpen}
        workspaceId={workspaceId}
        task={editingTask}
        agents={agents}
        onClose={() => setEditorOpen(false)}
        onSaved={handleSaved}
      />

      <TaskDetailsDrawer
        open={selectedTask !== null}
        workspaceId={workspaceId}
        task={selectedTask}
        events={selectedEvents}
        onClose={() => setSelectedTask(null)}
        onEdit={(task) => {
          setEditingTask(task);
          setEditorOpen(true);
        }}
        onCommand={handleCommand}
        onDeleted={handleDeleted}
        onChanged={(updated) => {
          handleSaved(updated);
          setSelectedTask(updated);
        }}
      />

      <AssignmentModal
        open={assignment !== null}
        mode={assignment?.mode ?? 'assign'}
        task={assignment?.task ?? null}
        agents={agents}
        onCancel={() => setAssignment(null)}
        onSubmit={handleAssignmentSubmit}
      />

      <Modal
        title="标记失败"
        open={markFailed !== null}
        onCancel={() => setMarkFailed(null)}
        footer={null}
      >
        <MarkFailedForm
          task={markFailed}
          onCancel={() => setMarkFailed(null)}
          onSubmit={async (reason) => {
            if (!markFailed) return;
            try {
              const result = await executeCommand(markFailed, 'MarkFailed', reason);
              applyCommandResult(result);
              message.success('已标记失败');
              setMarkFailed(null);
            } catch (error) {
              message.error(parseTaskError(error).body?.message ?? '标记失败');
            }
          }}
        />
      </Modal>
    </PageContainer>
  );
}

/** 任务看板模态窗口：约占窗口 80% 宽高，内容区可滚动，关闭即卸载并重新加载数据。 */
export function TaskBoardModal({
  open,
  workspaceId,
  onClose,
}: {
  open: boolean;
  workspaceId: string;
  onClose: () => void;
}) {
  return (
    <Modal
      title="任务看板"
      open={open}
      onCancel={onClose}
      footer={null}
      width="80vw"
      destroyOnClose
      styles={{
        body: {
          height: 'calc(80vh - 88px)',
          overflow: 'auto',
        },
      }}
    >
      <WorkspaceTasksPanel workspaceId={workspaceId} />
    </Modal>
  );
}

export default WorkspaceTasksPanel;

// ─── 指派/执行对话框（必选 agentId；RunNow 高峰二选一）──────────────────

function AssignmentModal({
  open,
  mode,
  task,
  agents,
  onCancel,
  onSubmit,
}: {
  open: boolean;
  mode: 'assign' | 'runNow';
  task: TaskDto | null;
  agents: WorkspaceAgentDto[];
  onCancel: () => void;
  onSubmit: (agentId: string, windowDecision?: WindowDecisionWire) => void;
}) {
  const [agentId, setAgentId] = useState<string | undefined>(undefined);
  const [windowDecision, setWindowDecision] = useState<WindowDecisionWire>(
    'deferred_peak_window',
  );
  const [submitting, setSubmitting] = useState(false);

  useEffect(() => {
    if (open) {
      setAgentId(undefined);
      setWindowDecision('deferred_peak_window');
    }
  }, [open]);

  const enabledAgents = agents.filter(
    (agent) => agent.isEnabled !== false && agent.isFrozen !== true,
  );
  const options = enabledAgents.map((agent) => ({
    value: agent.agentId,
    label: agent.displayName || agent.name || agent.agentId,
  }));

  const submit = async () => {
    if (!agentId) return;
    setSubmitting(true);
    await onSubmit(agentId, mode === 'runNow' ? windowDecision : undefined);
    setSubmitting(false);
  };

  return (
    <Modal
      title={mode === 'assign' ? '指派任务' : '执行任务'}
      open={open}
      onCancel={onCancel}
      onOk={submit}
      okText={mode === 'assign' ? '指派' : '执行'}
      cancelText="取消"
      confirmLoading={submitting}
      okButtonProps={{ disabled: !agentId }}
    >
      {task && (
        <div style={{ marginBottom: 12 }}>
          <Text strong>{task.title}</Text>
        </div>
      )}
      <Space direction="vertical" style={{ width: '100%' }}>
        <div>
          <Text>目标 Agent（必选）</Text>
          <Select
            showSearch
            optionFilterProp="label"
            placeholder="选择 Agent"
            style={{ width: '100%', marginTop: 4 }}
            options={options}
            value={agentId}
            onChange={setAgentId}
          />
        </div>
        {mode === 'runNow' && (
          <>
            <div>
              <Text>高峰窗口策略</Text>
              <Radio.Group
                style={{ display: 'block', marginTop: 4 }}
                value={windowDecision}
                onChange={(e) =>
                  setWindowDecision(e.target.value as WindowDecisionWire)
                }
              >
                <Space direction="vertical">
                  <Radio value="deferred_peak_window">等待空闲时段</Radio>
                  <Radio value="allowed_explicit_override">本次高峰运行</Radio>
                </Space>
              </Radio.Group>
            </div>
            <Text type="secondary" style={{ fontSize: 12 }}>
              提示：当前版本仅记录 windowDecision，峰谷执行判定由 TB-05/TB-07
              接入后生效。
            </Text>
          </>
        )}
      </Space>
    </Modal>
  );
}

function MarkFailedForm({
  task,
  onCancel,
  onSubmit,
}: {
  task: TaskDto | null;
  onCancel: () => void;
  onSubmit: (reason: string) => void;
}) {
  const [reason, setReason] = useState('');

  useEffect(() => {
    setReason('');
  }, [task]);

  return (
    <>
      <div style={{ marginBottom: 8 }}>
        <Text strong>{task?.title}</Text>
      </div>
      <Input.TextArea
        rows={3}
        placeholder="失败原因（必填）"
        value={reason}
        onChange={(e) => setReason(e.target.value)}
      />
      <Space style={{ marginTop: 12, justifyContent: 'flex-end', width: '100%' }}>
        <Button onClick={onCancel}>取消</Button>
        <Button
          type="primary"
          danger
          disabled={!reason.trim()}
          onClick={() => onSubmit(reason.trim())}
        >
          标记失败
        </Button>
      </Space>
    </>
  );
}
