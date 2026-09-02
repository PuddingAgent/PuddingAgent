import {
  CalendarOutlined,
  ClockCircleOutlined,
  EditOutlined,
  MoreOutlined,
  ThunderboltOutlined,
  UserOutlined,
} from '@ant-design/icons';
import { Button, Dropdown, Progress, Space, Tag, Typography } from 'antd';
import type { MenuProps } from 'antd';
import React from 'react';
import dayjs from 'dayjs';
import { PuddingStatusBadge } from '@/components';
import type { PuddingStatusTone } from '@/components';
import {
  canApplyCommand,
  canEditTask,
  TASK_PRIORITY_LABELS,
  TASK_STATUS_LABELS,
  type TaskCommandWire,
  type TaskDto,
  type TaskStatusWire,
} from './types';

const { Text } = Typography;

export interface TaskActions {
  onOpen: (task: TaskDto) => void;
  onEdit: (task: TaskDto) => void;
  onAssign: (task: TaskDto) => void;
  onRunNow: (task: TaskDto) => void;
  onToggleAutoDispatch: (task: TaskDto) => void;
  onCommand: (task: TaskDto, command: TaskCommandWire) => void;
}

const PRIORITY_COLORS: Record<TaskDto['priority'], string> = {
  p0: 'red',
  p1: 'orange',
  p2: 'blue',
  p3: 'default',
};

function statusTone(status: TaskStatusWire): PuddingStatusTone {
  switch (status) {
    case 'Completed':
      return 'success';
    case 'Failed':
    case 'Blocked':
      return 'danger';
    case 'InProgress':
    case 'Reserved':
    case 'Assigned':
      return 'accent';
    case 'Deferred':
    case 'NeedsReview':
      return 'warning';
    default:
      return 'neutral';
  }
}

function formatDue(dueAtUtc?: string): string | null {
  if (!dueAtUtc) return null;
  const parsed = dayjs(dueAtUtc);
  if (!parsed.isValid()) return null;
  return parsed.format('MM-DD HH:mm');
}

export interface TaskCardProps {
  task: TaskDto;
  actions: TaskActions;
}

/**
 * 任务卡片。Quiet UI（ST-08A.8）：默认只显示结论 + 原因 + 恢复动作，
 * 技术细节（traceId/绑定 ID/事件时间线）进 Details Drawer。
 */
export const TaskCard: React.FC<TaskCardProps> = ({ task, actions }) => {
  const due = formatDue(task.dueAtUtc);
  const showProgress = typeof task.progressPercent === 'number';

  const menuItems: MenuProps['items'] = [
    { key: 'open', label: '查看详情', onClick: () => actions.onOpen(task) },
    canEditTask(task.status)
      ? {
          key: 'edit',
          label: '编辑',
          icon: <EditOutlined />,
          onClick: () => actions.onEdit(task),
        }
      : null,
    canEditTask(task.status)
      ? {
          key: 'auto-dispatch',
          label: task.autoDispatchEnabled ? '退出自动调度' : '纳入自动调度',
          onClick: () => actions.onToggleAutoDispatch(task),
        }
      : null,
    canApplyCommand(task.status, 'Assign')
      ? {
          key: 'assign',
          label: '指派',
          icon: <UserOutlined />,
          onClick: () => actions.onAssign(task),
        }
      : null,
    canApplyCommand(task.status, 'RunNow')
      ? {
          key: 'run-now',
          label: '执行',
          icon: <ThunderboltOutlined />,
          onClick: () => actions.onRunNow(task),
        }
      : null,
    canApplyCommand(task.status, 'Reopen')
      ? {
          key: 'reopen',
          label: '重新打开',
          onClick: () => actions.onCommand(task, 'Reopen'),
        }
      : null,
    canApplyCommand(task.status, 'Resume')
      ? {
          key: 'resume',
          label: '恢复',
          onClick: () => actions.onCommand(task, 'Resume'),
        }
      : null,
    canApplyCommand(task.status, 'Requeue')
      ? {
          key: 'requeue',
          label: '重新排队',
          onClick: () => actions.onCommand(task, 'Requeue'),
        }
      : null,
    canApplyCommand(task.status, 'MarkFailed')
      ? {
          key: 'mark-failed',
          label: '标记失败',
          danger: true,
          onClick: () => actions.onCommand(task, 'MarkFailed'),
        }
      : null,
    canApplyCommand(task.status, 'Cancel')
      ? {
          key: 'cancel',
          label: '取消',
          danger: true,
          onClick: () => actions.onCommand(task, 'Cancel'),
        }
      : null,
    canApplyCommand(task.status, 'Archive')
      ? {
          key: 'archive',
          label: '归档',
          danger: true,
          onClick: () => actions.onCommand(task, 'Archive'),
        }
      : null,
  ].filter((item): item is NonNullable<typeof item> => item !== null);

  return (
    <div
      className="task-card"
      style={{
        background: 'var(--pudding-chat-bg, #fff)',
        border: '1px solid var(--pudding-chat-border, #f0f0f0)',
        borderRadius: 8,
        padding: '10px 12px',
        marginBottom: 8,
        cursor: 'pointer',
        transition: 'box-shadow 180ms ease',
      }}
      onClick={() => actions.onOpen(task)}
      role="button"
      tabIndex={0}
      onKeyDown={(e) => {
        if (e.key === 'Enter' || e.key === ' ') actions.onOpen(task);
      }}
      aria-label={task.title}
    >
      <Space
        direction="vertical"
        size={4}
        style={{ width: '100%' }}
      >
        <Space size={4} style={{ width: '100%' }}>
          <Tag
            color={PRIORITY_COLORS[task.priority]}
            style={{ marginInlineEnd: 0, fontSize: 11, lineHeight: '18px' }}
          >
            {TASK_PRIORITY_LABELS[task.priority]}
          </Tag>
          <PuddingStatusBadge tone={statusTone(task.status)}>
            {TASK_STATUS_LABELS[task.status]}
          </PuddingStatusBadge>
          {task.autoDispatchEnabled && (
            <Tag color="purple" style={{ marginInlineEnd: 0, fontSize: 11 }}>
              自动
            </Tag>
          )}
          <span style={{ flex: 1 }} />
          <Dropdown
            menu={{ items: menuItems }}
            trigger={['click']}
            placement="bottomRight"
          >
            <Button
              type="text"
              size="small"
              icon={<MoreOutlined />}
              aria-label="任务操作"
              onClick={(e) => e.stopPropagation()}
            />
          </Dropdown>
        </Space>

        <Text strong ellipsis={{ tooltip: task.title }}>
          {task.title}
        </Text>

        {task.description && (
          <Text type="secondary" ellipsis style={{ fontSize: 12 }}>
            {task.description}
          </Text>
        )}

        {task.status === 'Blocked' && (
          <Text type="danger" style={{ fontSize: 12 }}>
            阻塞：{task.blockerKind ?? ''} {task.blockerReason ?? ''}
          </Text>
        )}

        {task.status === 'Failed' && (
          <Text type="danger" style={{ fontSize: 12 }}>
            失败：{task.failureCode ?? ''} {task.failureReason ?? ''}
          </Text>
        )}

        {showProgress && (
          <Progress
            percent={Math.max(0, Math.min(100, task.progressPercent ?? 0))}
            size="small"
            status={task.status === 'Failed' ? 'exception' : 'active'}
          />
        )}

        <Space size={8} style={{ fontSize: 12, color: 'var(--pudding-chat-text-subtle, #999)' }}>
          {task.preferredAgentId && (
            <span>
              <UserOutlined /> {task.preferredAgentId}
            </span>
          )}
          {task.activeAssignmentId && (
            <span>
              <ThunderboltOutlined /> 已执行
            </span>
          )}
          {due && (
            <span>
              <CalendarOutlined /> {due}
            </span>
          )}
          {task.notBeforeUtc && (
            <span>
              <ClockCircleOutlined /> {formatDue(task.notBeforeUtc)}
            </span>
          )}
        </Space>
      </Space>
    </div>
  );
};

export default TaskCard;
