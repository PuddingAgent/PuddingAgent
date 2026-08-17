/**
 * workspace-tasks 前端类型契约（TB-04）
 *
 * wire 值唯一权威：Source/PuddingCore/Tasks/WorkspaceTaskModels.cs（TB-01 已冻结）
 * + temp/tb03-crud-api-contract-20260816.md §四。前端不得自造 wire 值；未知值
 * fail-closed 由后端返回 409/422，前端按错误协议展示。
 *
 * 本文件只声明 wire 联合、DTO 与纯映射/工具函数，不实现任何状态机分派逻辑
 * （ADR-073 §6 不变量 1/3：列归属只消费 TaskDto.boardColumn）。
 */

// ─── wire 枚举（string literal union，值注释对齐 PuddingCode.Tasks）─────────

/** 12 态（WorkspaceTaskStatus） */
export const TASK_STATUS_WIRES = [
  'Backlog',
  'Ready',
  'Deferred',
  'Reserved',
  'Assigned',
  'NeedsReview',
  'InProgress',
  'Blocked',
  'Completed',
  'Failed',
  'Cancelled',
  'Archived',
] as const;
export type TaskStatusWire = (typeof TASK_STATUS_WIRES)[number];

/** 5 列投影（BoardColumn） */
export const BOARD_COLUMN_WIRES = [
  'Backlog',
  'Todo',
  'InProgress',
  'Done',
  'Failed',
] as const;
export type BoardColumnWire = (typeof BOARD_COLUMN_WIRES)[number];

/** 4 优先级（TaskPriority） */
export const TASK_PRIORITY_WIRES = ['p0', 'p1', 'p2', 'p3'] as const;
export type TaskPriorityWire = (typeof TASK_PRIORITY_WIRES)[number];

/** 3 执行窗口（TaskExecutionWindow） */
export const TASK_EXECUTION_WINDOW_WIRES = [
  'inherit',
  'anytime',
  'off_peak_only',
] as const;
export type TaskExecutionWindowWire =
  (typeof TASK_EXECUTION_WINDOW_WIRES)[number];

/** 10 命令（TaskCommand） */
export const TASK_COMMAND_WIRES = [
  'Create',
  'Update',
  'Assign',
  'RunNow',
  'Cancel',
  'Archive',
  'Reopen',
  'MarkFailed',
  'Resume',
  'Requeue',
] as const;
export type TaskCommandWire = (typeof TASK_COMMAND_WIRES)[number];

/** 18 错误码（TaskErrorCode） */
export const TASK_ERROR_CODE_WIRES = [
  'task.not_found',
  'task.version_conflict',
  'task.state_conflict',
  'task.invalid_transition',
  'task.invalid_disposition',
  'task.reason_required',
  'task.result_required',
  'task.artifact_required',
  'task.not_reopenable',
  'task.cannot_hard_delete',
  'assignment.not_found',
  'assignment.already_active',
  'assignment.stale',
  'agent.not_found',
  'agent.unavailable',
  'capability.missing',
  'policy.invalid',
  'policy.version_conflict',
] as const;
export type TaskErrorCodeWire = (typeof TASK_ERROR_CODE_WIRES)[number];

/** 17 事件类型（TaskEventType，task.* 子集） */
export const TASK_EVENT_TYPE_WIRES = [
  'task.created',
  'task.updated',
  'task.ready',
  'task.deferred',
  'task.reserved',
  'task.assigned',
  'task.accepted',
  'task.progressed',
  'task.blocked',
  'task.assignment_rejected',
  'task.completed',
  'task.failed',
  'task.reopened',
  'task.cancelled',
  'task.archived',
  'task.dispatch.requested',
  'task.dispatch.deferred',
] as const;
export type TaskEventTypeWire = (typeof TASK_EVENT_TYPE_WIRES)[number];

/** 15 高峰决策码（DecisionCode） */
export const WINDOW_DECISION_WIRES = [
  'allowed_user_direct',
  'allowed_off_peak',
  'allowed_priority_bypass',
  'allowed_explicit_override',
  'deferred_peak_window',
  'deferred_not_before',
  'deferred_agent_busy',
  'deferred_agent_offline',
  'deferred_agent_cooldown',
  'deferred_user_message_pending',
  'denied_policy_invalid',
  'denied_task_state_changed',
  'denied_stale_assignment',
  'denied_workspace_frozen',
  'denied_agent_frozen',
] as const;
export type WindowDecisionWire = (typeof WINDOW_DECISION_WIRES)[number];

// ─── DTO（与 TB-03 §四 TaskDto 逐字段对齐）───────────────────────────────

export interface TaskDto {
  taskId: string;
  workspaceId: string;
  title: string;
  description?: string;
  acceptanceCriteria?: string;
  /** wire: "Backlog"/"Ready"/.../"Archived" */
  status: TaskStatusWire;
  /** wire: "Backlog"/"Todo"/"InProgress"/"Done"/"Failed" */
  boardColumn: BoardColumnWire;
  /** 当前状态可迁移到的目标状态 wire 列表（后端派生，前端只消费不实现状态机） */
  allowedTransitions: TaskStatusWire[];
  /** wire: "p0"/"p1"/"p2"/"p3" */
  priority: TaskPriorityWire;
  /** wire: "inherit"/"anytime"/"off_peak_only" */
  executionWindow: TaskExecutionWindowWire;
  preferredAgentId?: string;
  activeAssignmentId?: string;
  /** ISO8601 UTC */
  notBeforeUtc?: string;
  dueAtUtc?: string;
  nextEligibleAtUtc?: string;
  sortOrder: number;
  progressPercent?: number;
  progressSummary?: string;
  blockerKind?: string;
  blockerReason?: string;
  failureCode?: string;
  failureReason?: string;
  /** CAS 乐观锁 */
  version: number;
  createdBy?: string;
  updatedBy?: string;
  createdAtUtc: string;
  updatedAtUtc: string;
  completedAtUtc?: string;
  failedAtUtc?: string;
  archivedAtUtc?: string;
}

export interface TaskPageDto {
  items: TaskDto[];
  /** keyset 游标 "{sort_order}|{task_id}"，无更多为 null */
  nextCursor: string | null;
}

// ─── 评论/备注（TB-12 F-1，对齐 TB-11 B-5 DTO）─────────────────────────

export type TaskCommentAuthorKindWire = 'user' | 'agent' | 'system';

export interface TaskCommentDto {
  commentId: string;
  taskId: string;
  workspaceId: string;
  authorKind: TaskCommentAuthorKindWire;
  authorId?: string;
  content: string;
  createdAtUtc: string;
}

export interface CreateTaskCommentRequest {
  content: string;
  authorKind?: TaskCommentAuthorKindWire;
}

// ─── 请求 DTO（TB-03 §四）───────────────────────────────────────────────

export interface CreateTaskRequest {
  title: string;
  description?: string;
  acceptanceCriteria?: string;
  /** 默认 "p3" */
  priority?: TaskPriorityWire;
  /** 默认 "inherit" */
  executionWindow?: TaskExecutionWindowWire;
  preferredAgentId?: string;
  notBeforeUtc?: string;
  dueAtUtc?: string;
  /** 默认 0 */
  sortOrder?: number;
}

export interface PatchTaskRequest {
  /** 必填（CAS） */
  expectedVersion: number;
  title?: string;
  description?: string;
  acceptanceCriteria?: string;
  /** 状态流转（TB-12 F-3，后端 PATCH 已支持，前端只透传目标状态） */
  status?: TaskStatusWire;
  priority?: TaskPriorityWire;
  executionWindow?: TaskExecutionWindowWire;
  preferredAgentId?: string;
  notBeforeUtc?: string;
  dueAtUtc?: string;
  sortOrder?: number;
}

export interface AssignTaskRequest {
  agentId: string;
  expectedVersion: number;
}

export interface RunNowTaskRequest {
  agentId: string;
  expectedVersion: number;
  /** 记录用，TB-03 不判定（TB-05/TB-07 接 Fence） */
  windowDecision?: WindowDecisionWire;
}

export interface CommandTaskRequest {
  expectedVersion: number;
  reason?: string;
}

// ─── 错误协议（TB-03 §五）───────────────────────────────────────────────

export interface TaskErrorResponse {
  /** 稳定 code，如 "task.version_conflict" */
  code: TaskErrorCodeWire;
  /** 面向操作者 */
  message: string;
  traceId: string;
  /** CAS 冲突时当前 version */
  version?: number;
  expectedVersion?: number;
  actualVersion?: number;
}

// ─── Watch 事件（TB-04 §5.3，SSE task.event 的 data）─────────────────────

export interface TaskEventWatchEvent {
  taskId: string;
  workspaceId: string;
  /** 全局游标（= SSE id） */
  sequence: number;
  /** task.created / task.updated / task.completed ... */
  eventType: TaskEventTypeWire;
  assignmentId?: string;
  agentId?: string;
  deliveryId?: string;
  executionId?: string;
  sessionId?: string;
  /** 事件发生时任务版本（用于 CAS 提示） */
  version?: number;
  createdAtUtc: string;
  /**
   * 最新看板列/状态（TB-04 §6 “以 event 携带的最新 boardColumn 为准”）。
   * 可选：若缺失，前端回退为 getTask 拉取权威 TaskDto.boardColumn。
   */
  boardColumn?: BoardColumnWire;
  status?: TaskStatusWire;
}

// ─── 列表查询参数（TB-04 §5.2）──────────────────────────────────────────

export interface ListTasksParams {
  status?: TaskStatusWire;
  boardColumn?: BoardColumnWire;
  agentId?: string;
  priority?: TaskPriorityWire;
  /** 默认 100，1-500 */
  limit?: number;
  /** "{sort_order}|{task_id}" */
  cursor?: string;
}

// ─── 看板列投影（只读文案表，不用于分派逻辑）────────────────────────────

export const BOARD_COLUMN_ORDER: readonly BoardColumnWire[] = [
  'Backlog',
  'Todo',
  'InProgress',
  'Done',
  'Failed',
];

/** boardColumn → 状态集合（权威 = TaskStateMachine.ProjectBoardColumn 反查） */
export const BOARD_COLUMN_STATUSES: Record<
  BoardColumnWire,
  readonly TaskStatusWire[]
> = {
  Backlog: ['Backlog'],
  Todo: ['Ready', 'Deferred', 'Reserved', 'Assigned', 'NeedsReview'],
  InProgress: ['InProgress', 'Blocked'],
  Done: ['Completed'],
  Failed: ['Failed'],
};

export const BOARD_COLUMN_LABELS: Record<BoardColumnWire, string> = {
  Backlog: '待规划',
  Todo: '待办',
  InProgress: '进行中',
  Done: '已完成',
  Failed: '已失败',
};

/** 状态 → 列徽标文案（用于卡片状态徽标，非分派） */
export const TASK_STATUS_LABELS: Record<TaskStatusWire, string> = {
  Backlog: '待规划',
  Ready: '待办',
  Deferred: '已推迟',
  Reserved: '已预留',
  Assigned: '已分配',
  NeedsReview: '需复盘',
  InProgress: '进行中',
  Blocked: '已阻塞',
  Completed: '已完成',
  Failed: '已失败',
  Cancelled: '已取消',
  Archived: '已归档',
};

export const TASK_PRIORITY_LABELS: Record<TaskPriorityWire, string> = {
  p0: 'P0',
  p1: 'P1',
  p2: 'P2',
  p3: 'P3',
};

export const TASK_EXECUTION_WINDOW_LABELS: Record<
  TaskExecutionWindowWire,
  string
> = {
  inherit: '继承策略',
  anytime: '任意时间',
  off_peak_only: '仅低峰',
};

// ─── 命令可用性（TB-04 §4.4，只读映射，UI 动作按钮显隐依据）────────────

export const TERMINAL_STATUSES: readonly TaskStatusWire[] = [
  'Completed',
  'Failed',
  'Cancelled',
  'Archived',
];

export const NON_TERMINAL_STATUSES: readonly TaskStatusWire[] = [
  'Backlog',
  'Ready',
  'Deferred',
  'Reserved',
  'Assigned',
  'NeedsReview',
  'InProgress',
  'Blocked',
];

export function isTerminalStatus(status: TaskStatusWire): boolean {
  return (TERMINAL_STATUSES as readonly string[]).includes(status);
}

export interface CommandDescriptor {
  command: TaskCommandWire;
  fromStatuses: readonly TaskStatusWire[];
  targetStatus: TaskStatusWire;
  label: string;
  destructive?: boolean;
}

/** 命令 → 可用性表（权威 = TaskStateMachine.TryApplyCommand，TB-01 §4.3） */
export const COMMAND_TABLE: readonly CommandDescriptor[] = [
  {
    command: 'Assign',
    fromStatuses: ['Ready'],
    targetStatus: 'Reserved',
    label: '指派',
  },
  {
    command: 'RunNow',
    fromStatuses: ['Ready', 'Deferred'],
    targetStatus: 'Reserved',
    label: '执行',
  },
  {
    command: 'Cancel',
    fromStatuses: ['Ready', 'Assigned', 'InProgress', 'Blocked'],
    targetStatus: 'Cancelled',
    label: '取消',
    destructive: true,
  },
  {
    command: 'Archive',
    fromStatuses: ['Completed', 'Cancelled', 'Failed'],
    targetStatus: 'Archived',
    label: '归档',
    destructive: true,
  },
  {
    command: 'Reopen',
    fromStatuses: ['Failed'],
    targetStatus: 'Ready',
    label: '重新打开',
  },
  {
    command: 'MarkFailed',
    fromStatuses: ['Assigned', 'InProgress', 'Blocked'],
    targetStatus: 'Failed',
    label: '标记失败',
    destructive: true,
  },
  {
    command: 'Resume',
    fromStatuses: ['Blocked', 'NeedsReview'],
    targetStatus: 'Ready',
    label: '恢复',
  },
  {
    command: 'Requeue',
    fromStatuses: ['Deferred', 'Ready'],
    targetStatus: 'Ready',
    label: '重新排队',
  },
];

export function canApplyCommand(
  status: TaskStatusWire,
  command: TaskCommandWire,
): boolean {
  const descriptor = COMMAND_TABLE.find((item) => item.command === command);
  if (!descriptor) return false;
  return (descriptor.fromStatuses as readonly string[]).includes(status);
}

/** 返回某状态可用的命令列表（前端动作按钮显隐依据） */
export function availableCommandsForStatus(
  status: TaskStatusWire,
): TaskCommandWire[] {
  return COMMAND_TABLE.filter((item) =>
    (item.fromStatuses as readonly string[]).includes(status),
  ).map((item) => item.command);
}

/** 编辑（Update 命令）可用性：任意非终态 */
export function canEditTask(status: TaskStatusWire): boolean {
  return !isTerminalStatus(status);
}

// ─── 错误码 → HTTP（TB-04 §5.4 / TB-03 §五）─────────────────────────────

export const TASK_ERROR_HTTP_STATUS: Record<TaskErrorCodeWire, number> = {
  'task.not_found': 404,
  'task.version_conflict': 409,
  'task.state_conflict': 409,
  'task.invalid_transition': 422,
  'task.invalid_disposition': 422,
  'task.reason_required': 422,
  'task.result_required': 422,
  'task.artifact_required': 422,
  'task.not_reopenable': 422,
  'task.cannot_hard_delete': 422,
  'assignment.not_found': 404,
  'assignment.already_active': 409,
  'assignment.stale': 409,
  'agent.not_found': 404,
  'agent.unavailable': 409,
  'capability.missing': 403,
  'policy.invalid': 422,
  'policy.version_conflict': 409,
};

export interface ParsedTaskError {
  httpStatus: number;
  body?: TaskErrorResponse;
}

/** 从 axios 错误对象提取 TaskErrorResponse（TB-04 §5.4） */
export function parseTaskError(error: unknown): ParsedTaskError {
  const candidate = error as {
    response?: { status?: unknown; data?: unknown };
  };
  const httpStatus =
    typeof candidate?.response?.status === 'number'
      ? candidate.response.status
      : 0;
  const data = candidate?.response?.data;
  if (data && typeof data === 'object') {
    const body = data as Partial<TaskErrorResponse>;
    if (typeof body.code === 'string' && typeof body.message === 'string') {
      return { httpStatus, body: data as TaskErrorResponse };
    }
  }
  return { httpStatus };
}

// ─── 看板切片与纯 upsert/remove 工具（不实现状态机）─────────────────────

export interface ColumnSlice {
  items: TaskDto[];
  nextCursor: string | null;
  loading: boolean;
  loadingMore: boolean;
  hasMore: boolean;
}

export type TaskColumns = Record<BoardColumnWire, ColumnSlice>;

export function emptyColumnSlice(): ColumnSlice {
  return {
    items: [],
    nextCursor: null,
    loading: false,
    loadingMore: false,
    hasMore: false,
  };
}

export function emptyColumns(): TaskColumns {
  return {
    Backlog: emptyColumnSlice(),
    Todo: emptyColumnSlice(),
    InProgress: emptyColumnSlice(),
    Done: emptyColumnSlice(),
    Failed: emptyColumnSlice(),
  };
}

/**
 * 将 TaskDto 放置到其 boardColumn 所属列，并从其他列移除同 taskId 的旧条目。
 * 列内按 sortOrder 升序（新任务 sortOrder=0 置顶），同值按 taskId 稳定排序。
 */
export function upsertTaskIntoColumns(
  columns: TaskColumns,
  task: TaskDto,
): TaskColumns {
  const next: TaskColumns = { ...columns };
  for (const column of BOARD_COLUMN_ORDER) {
    next[column] = {
      ...columns[column],
      items: columns[column].items.filter((item) => item.taskId !== task.taskId),
    };
  }
  const target = task.boardColumn;
  if (!(BOARD_COLUMN_ORDER as readonly string[]).includes(target)) {
    return next;
  }
  const slice = next[target];
  const items = [...slice.items, task].sort(
    (a, b) => a.sortOrder - b.sortOrder || a.taskId.localeCompare(b.taskId),
  );
  next[target] = { ...slice, items };
  return next;
}

/** 从所有列移除指定任务（Cancelled/Archived → 历史，不占五列） */
export function removeTaskFromColumns(
  columns: TaskColumns,
  taskId: string,
): TaskColumns {
  const next: TaskColumns = { ...columns };
  for (const column of BOARD_COLUMN_ORDER) {
    next[column] = {
      ...columns[column],
      items: columns[column].items.filter((item) => item.taskId !== taskId),
    };
  }
  return next;
}
