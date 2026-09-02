import {
  BOARD_COLUMN_ORDER,
  BOARD_COLUMN_STATUSES,
  BOARD_COLUMN_WIRES,
  canApplyCommand,
  canEditTask,
  availableCommandsForStatus,
  emptyColumns,
  removeTaskFromColumns,
  upsertTaskIntoColumns,
  TASK_COMMAND_WIRES,
  TASK_ERROR_CODE_WIRES,
  TASK_ERROR_HTTP_STATUS,
  TASK_EVENT_TYPE_WIRES,
  TASK_EXECUTION_WINDOW_WIRES,
  TASK_PRIORITY_WIRES,
  TASK_STATUS_WIRES,
  WINDOW_DECISION_WIRES,
  isTerminalStatus,
  type TaskDto,
} from './types';

function makeTask(overrides: Partial<TaskDto> = {}): TaskDto {
  return {
    taskId: 'task-1',
    workspaceId: 'default',
    title: '示例任务',
    status: 'Backlog',
    boardColumn: 'Backlog',
    allowedTransitions: [],
    priority: 'p3',
    executionWindow: 'inherit',
    taskType: 'general',
    requiredCapabilityIds: [],
    allowAgentFallback: true,
    autoDispatchEnabled: false,
    sortOrder: 0,
    version: 1,
    createdAtUtc: '2026-08-16T00:00:00Z',
    updatedAtUtc: '2026-08-16T00:00:00Z',
    ...overrides,
  };
}

describe('wire 联合穷举（对齐 PuddingCode.Tasks 注释值）', () => {
  it('12 态', () => {
    expect(TASK_STATUS_WIRES).toEqual([
      'Backlog', 'Ready', 'Deferred', 'Reserved', 'Assigned', 'NeedsReview',
      'InProgress', 'Blocked', 'Completed', 'Failed', 'Cancelled', 'Archived',
    ]);
    expect(TASK_STATUS_WIRES).toHaveLength(12);
  });

  it('5 列', () => {
    expect(BOARD_COLUMN_WIRES).toEqual([
      'Backlog', 'Todo', 'InProgress', 'Done', 'Failed',
    ]);
    expect(BOARD_COLUMN_WIRES).toHaveLength(5);
    expect(BOARD_COLUMN_ORDER).toEqual([
      'Backlog', 'Todo', 'InProgress', 'Done', 'Failed',
    ]);
  });

  it('4 优先级', () => {
    expect(TASK_PRIORITY_WIRES).toEqual(['p0', 'p1', 'p2', 'p3']);
    expect(TASK_PRIORITY_WIRES).toHaveLength(4);
  });

  it('3 执行窗口', () => {
    expect(TASK_EXECUTION_WINDOW_WIRES).toEqual([
      'inherit', 'anytime', 'off_peak_only',
    ]);
    expect(TASK_EXECUTION_WINDOW_WIRES).toHaveLength(3);
  });

  it('10 命令', () => {
    expect(TASK_COMMAND_WIRES).toEqual([
      'Create', 'Update', 'Assign', 'RunNow', 'Cancel', 'Archive',
      'Reopen', 'MarkFailed', 'Resume', 'Requeue',
    ]);
    expect(TASK_COMMAND_WIRES).toHaveLength(10);
  });

  it('18 错误码', () => {
    expect(TASK_ERROR_CODE_WIRES).toHaveLength(18);
    expect(TASK_ERROR_CODE_WIRES).toContain('task.version_conflict');
    expect(TASK_ERROR_CODE_WIRES).toContain('agent.unavailable');
  });

  it('17 事件类型', () => {
    expect(TASK_EVENT_TYPE_WIRES).toHaveLength(17);
    expect(TASK_EVENT_TYPE_WIRES).toContain('task.created');
    expect(TASK_EVENT_TYPE_WIRES).toContain('task.dispatch.deferred');
  });

  it('15 高峰决策码', () => {
    expect(WINDOW_DECISION_WIRES).toHaveLength(15);
  });
});

describe('看板列投影（只读文案表）', () => {
  it('boardColumn → 状态集合', () => {
    expect(BOARD_COLUMN_STATUSES.Backlog).toEqual(['Backlog']);
    expect(BOARD_COLUMN_STATUSES.Todo).toEqual([
      'Ready', 'Deferred', 'Reserved', 'Assigned', 'NeedsReview',
    ]);
    expect(BOARD_COLUMN_STATUSES.InProgress).toEqual(['InProgress', 'Blocked']);
    expect(BOARD_COLUMN_STATUSES.Done).toEqual(['Completed']);
    expect(BOARD_COLUMN_STATUSES.Failed).toEqual(['Failed']);
  });
});

describe('命令可用性表（TaskStateMachine.TryApplyCommand）', () => {
  it('RunNow 前置 Ready/Deferred', () => {
    expect(canApplyCommand('Ready', 'RunNow')).toBe(true);
    expect(canApplyCommand('Deferred', 'RunNow')).toBe(true);
    expect(canApplyCommand('Backlog', 'RunNow')).toBe(false);
  });

  it('Reopen 仅 Failed', () => {
    expect(canApplyCommand('Failed', 'Reopen')).toBe(true);
    expect(canApplyCommand('Ready', 'Reopen')).toBe(false);
    expect(canApplyCommand('Completed', 'Reopen')).toBe(false);
  });

  it('Cancel 前置 Ready/Assigned/InProgress/Blocked', () => {
    expect(canApplyCommand('Ready', 'Cancel')).toBe(true);
    expect(canApplyCommand('InProgress', 'Cancel')).toBe(true);
    expect(canApplyCommand('Backlog', 'Cancel')).toBe(false);
    expect(canApplyCommand('Completed', 'Cancel')).toBe(false);
  });

  it('Archive 前置 Completed/Cancelled/Failed', () => {
    expect(canApplyCommand('Completed', 'Archive')).toBe(true);
    expect(canApplyCommand('Failed', 'Archive')).toBe(true);
    expect(canApplyCommand('Backlog', 'Archive')).toBe(false);
  });

  it('Resume 前置 Blocked/NeedsReview；Requeue 前置 Deferred/Ready', () => {
    expect(canApplyCommand('Blocked', 'Resume')).toBe(true);
    expect(canApplyCommand('NeedsReview', 'Resume')).toBe(true);
    expect(canApplyCommand('Deferred', 'Requeue')).toBe(true);
    expect(canApplyCommand('Ready', 'Requeue')).toBe(true);
  });

  it('MarkFailed 前置 Assigned/InProgress/Blocked', () => {
    expect(canApplyCommand('InProgress', 'MarkFailed')).toBe(true);
    expect(canApplyCommand('Backlog', 'MarkFailed')).toBe(false);
  });

  it('Assign 仅 Ready', () => {
    expect(canApplyCommand('Ready', 'Assign')).toBe(true);
    expect(canApplyCommand('Backlog', 'Assign')).toBe(false);
  });

  it('availableCommandsForStatus 返回可用命令子集', () => {
    expect(availableCommandsForStatus('Failed')).toContain('Reopen');
    expect(availableCommandsForStatus('Failed')).toContain('Archive');
    expect(availableCommandsForStatus('Failed')).not.toContain('RunNow');
  });

  it('canEditTask：任意非终态可编辑', () => {
    expect(canEditTask('Backlog')).toBe(true);
    expect(canEditTask('InProgress')).toBe(true);
    expect(canEditTask('Completed')).toBe(false);
    expect(canEditTask('Failed')).toBe(false);
    expect(canEditTask('Cancelled')).toBe(false);
    expect(canEditTask('Archived')).toBe(false);
    expect(isTerminalStatus('Completed')).toBe(true);
    expect(isTerminalStatus('InProgress')).toBe(false);
  });
});

describe('错误码 → HTTP 状态', () => {
  it('404 组', () => {
    expect(TASK_ERROR_HTTP_STATUS['task.not_found']).toBe(404);
    expect(TASK_ERROR_HTTP_STATUS['assignment.not_found']).toBe(404);
    expect(TASK_ERROR_HTTP_STATUS['agent.not_found']).toBe(404);
  });
  it('409 组', () => {
    expect(TASK_ERROR_HTTP_STATUS['task.version_conflict']).toBe(409);
    expect(TASK_ERROR_HTTP_STATUS['task.state_conflict']).toBe(409);
    expect(TASK_ERROR_HTTP_STATUS['agent.unavailable']).toBe(409);
    expect(TASK_ERROR_HTTP_STATUS['policy.version_conflict']).toBe(409);
  });
  it('422 组', () => {
    expect(TASK_ERROR_HTTP_STATUS['task.invalid_transition']).toBe(422);
    expect(TASK_ERROR_HTTP_STATUS['task.reason_required']).toBe(422);
    expect(TASK_ERROR_HTTP_STATUS['task.not_reopenable']).toBe(422);
  });
  it('403 组', () => {
    expect(TASK_ERROR_HTTP_STATUS['capability.missing']).toBe(403);
  });
});

describe('看板切片 upsert / remove（只消费 boardColumn，不实现状态机）', () => {
  it('upsert 放入正确列', () => {
    const columns = emptyColumns();
    const next = upsertTaskIntoColumns(columns, makeTask({ boardColumn: 'Backlog' }));
    expect(next.Backlog.items).toHaveLength(1);
    expect(next.Todo.items).toHaveLength(0);
  });

  it('跨列移动：从旧列移除、加入新列', () => {
    let columns = emptyColumns();
    columns = upsertTaskIntoColumns(columns, makeTask({ taskId: 't', boardColumn: 'Todo' }));
    expect(columns.Todo.items).toHaveLength(1);

    const moved = upsertTaskIntoColumns(
      columns,
      makeTask({ taskId: 't', boardColumn: 'InProgress' }),
    );
    expect(moved.Todo.items).toHaveLength(0);
    expect(moved.InProgress.items).toHaveLength(1);
  });

  it('同列 upsert 替换旧版本（不重复）', () => {
    let columns = emptyColumns();
    columns = upsertTaskIntoColumns(columns, makeTask({ taskId: 't', version: 1, sortOrder: 5 }));
    columns = upsertTaskIntoColumns(columns, makeTask({ taskId: 't', version: 2, sortOrder: 5 }));
    expect(columns.Backlog.items).toHaveLength(1);
    expect(columns.Backlog.items[0].version).toBe(2);
  });

  it('按 sortOrder 升序排列', () => {
    let columns = emptyColumns();
    columns = upsertTaskIntoColumns(columns, makeTask({ taskId: 'b', sortOrder: 10 }));
    columns = upsertTaskIntoColumns(columns, makeTask({ taskId: 'a', sortOrder: 0 }));
    expect(columns.Backlog.items.map((t) => t.taskId)).toEqual(['a', 'b']);
  });

  it('remove 从所有列移除', () => {
    let columns = emptyColumns();
    columns = upsertTaskIntoColumns(columns, makeTask({ taskId: 't', boardColumn: 'Todo' }));
    const removed = removeTaskFromColumns(columns, 't');
    for (const column of BOARD_COLUMN_ORDER) {
      expect(removed[column].items).toHaveLength(0);
    }
  });
});
