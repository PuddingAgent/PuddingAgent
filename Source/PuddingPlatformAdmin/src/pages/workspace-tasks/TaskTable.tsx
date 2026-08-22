import { DeleteOutlined } from '@ant-design/icons';
import { App, Button, Select, Space, Tag, Typography } from 'antd';
import type { ColumnsType } from 'antd/es/table';
import React, { useMemo, useState } from 'react';
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
  /** 批量移除（智能删除：无历史 Backlog 硬删，其余归档软删）。 */
  onBatchRemove: (tasks: TaskDto[]) => void;
  /** 批量设置状态（逐任务 PATCH status，失败者单独提示）。 */
  onBatchStatus: (tasks: TaskDto[], status: TaskStatusWire) => void;
}

/** 紧凑列表视图（antd Table）+ 勾选批量操作（移除/设置状态）。 */
export const TaskTable: React.FC<TaskTableProps> = ({
  items,
  loading,
  onOpen,
  onBatchRemove,
  onBatchStatus,
}) => {
  const { modal } = App.useApp();
  const [selectedRowKeys, setSelectedRowKeys] = useState<React.Key[]>([]);
  const [batchStatus, setBatchStatus] = useState<TaskStatusWire | undefined>(
    undefined,
  );

  // 选中行可能因刷新/翻页而消失，始终基于当前 items 解析。
  const selectedTasks = useMemo(() => {
    const keySet = new Set(selectedRowKeys.map(String));
    return items.filter((task) => keySet.has(task.taskId));
  }, [items, selectedRowKeys]);

  const confirmRemove = () => {
    if (selectedTasks.length === 0) return;
    modal.confirm({
      title: `移除所选 ${selectedTasks.length} 个任务？`,
      content:
        '无历史 Backlog 任务将永久删除；其余任务将归档（软删除，保留审计历史）。',
      okText: '移除',
      okType: 'danger',
      cancelText: '取消',
      onOk: () => {
        onBatchRemove(selectedTasks);
        setSelectedRowKeys([]);
      },
    });
  };

  const applyBatchStatus = () => {
    if (!batchStatus || selectedTasks.length === 0) return;
    onBatchStatus(selectedTasks, batchStatus);
    setSelectedRowKeys([]);
    setBatchStatus(undefined);
  };

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
    <div>
      {selectedTasks.length > 0 && (
        <Space
          style={{ marginBottom: 12, width: '100%', justifyContent: 'flex-end' }}
          wrap
        >
          <Text type="secondary">已选 {selectedTasks.length} 项</Text>
          <Select<TaskStatusWire>
            allowClear
            placeholder="批量设置状态"
            style={{ width: 160 }}
            value={batchStatus}
            onChange={(value?: TaskStatusWire) => setBatchStatus(value)}
            options={Object.entries(TASK_STATUS_LABELS).map(([value, label]) => ({
              value: value as TaskStatusWire,
              label,
            }))}
          />
          <Button
            type="primary"
            disabled={!batchStatus}
            onClick={applyBatchStatus}
            data-testid="batch-status-apply"
          >
            应用状态
          </Button>
          <Button
            danger
            icon={<DeleteOutlined />}
            onClick={confirmRemove}
            data-testid="batch-remove"
          >
            移除所选
          </Button>
        </Space>
      )}
      <PuddingDataTable<TaskDto>
        rowKey="taskId"
        columns={columns}
        dataSource={items}
        loading={loading}
        emptyText="暂无任务"
        pagination={false}
        rowSelection={{
          selectedRowKeys,
          onChange: (keys) => setSelectedRowKeys(keys),
        }}
      />
    </div>
  );
};

export default TaskTable;
