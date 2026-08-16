import { Button, Tag, Typography } from 'antd';
import type { ColumnsType } from 'antd/es/table';
import React from 'react';
import dayjs from 'dayjs';
import { PuddingDataTable, PuddingStatusBadge } from '@/components';
import type { PuddingStatusTone } from '@/components';
import {
  BOARD_COLUMN_LABELS,
  TASK_PRIORITY_LABELS,
  TASK_STATUS_LABELS,
  type TaskDto,
  type TaskStatusWire,
} from './types';

const { Text } = Typography;

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

function formatUtc(value?: string): string {
  if (!value) return '—';
  const parsed = dayjs(value);
  return parsed.isValid() ? parsed.format('YYYY-MM-DD HH:mm') : '—';
}

export interface TaskTableProps {
  items: TaskDto[];
  loading?: boolean;
  onOpen: (task: TaskDto) => void;
}

/** 紧凑列表视图（antd Table）。 */
export const TaskTable: React.FC<TaskTableProps> = ({
  items,
  loading,
  onOpen,
}) => {
  const columns: ColumnsType<TaskDto> = [
    {
      title: '标题',
      dataIndex: 'title',
      key: 'title',
      ellipsis: true,
      render: (title: string, record) => (
        <Button type="link" size="small" onClick={() => onOpen(record)}>
          {title}
        </Button>
      ),
    },
    {
      title: '优先级',
      dataIndex: 'priority',
      key: 'priority',
      width: 80,
      render: (priority: TaskDto['priority']) => (
        <Tag color={PRIORITY_COLORS[priority]}>{TASK_PRIORITY_LABELS[priority]}</Tag>
      ),
    },
    {
      title: '状态',
      dataIndex: 'status',
      key: 'status',
      width: 110,
      render: (status: TaskStatusWire) => (
        <PuddingStatusBadge tone={statusTone(status)}>
          {TASK_STATUS_LABELS[status]}
        </PuddingStatusBadge>
      ),
    },
    {
      title: '列',
      dataIndex: 'boardColumn',
      key: 'boardColumn',
      width: 90,
      render: (column: TaskDto['boardColumn']) => (
        <Text type="secondary">{BOARD_COLUMN_LABELS[column]}</Text>
      ),
    },
    {
      title: 'Agent',
      dataIndex: 'preferredAgentId',
      key: 'agent',
      width: 180,
      ellipsis: true,
      render: (agentId?: string) => agentId ?? '—',
    },
    {
      title: '截止',
      dataIndex: 'dueAtUtc',
      key: 'due',
      width: 150,
      render: (due?: string) => formatUtc(due),
    },
    {
      title: '更新时间',
      dataIndex: 'updatedAtUtc',
      key: 'updatedAt',
      width: 150,
      render: (updatedAt: string) => formatUtc(updatedAt),
    },
  ];

  return (
    <PuddingDataTable<TaskDto>
      rowKey="taskId"
      columns={columns}
      dataSource={items}
      loading={loading}
      emptyText="暂无任务"
      pagination={false}
    />
  );
};

export default TaskTable;
