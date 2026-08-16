import {
  App,
  Button,
  Descriptions,
  Drawer,
  Space,
  Tag,
  Typography,
} from 'antd';
import React from 'react';
import dayjs from 'dayjs';
import { deleteTask } from '@/services/platform/api';
import { TaskEventTimeline } from './TaskEventTimeline';
import { TaskExecutionLink } from './TaskExecutionLink';
import {
  parseTaskError,
  TASK_EXECUTION_WINDOW_LABELS,
  TASK_PRIORITY_LABELS,
  TASK_STATUS_LABELS,
  type TaskCommandWire,
  type TaskDto,
  type TaskEventWatchEvent,
} from './types';

const { Text } = Typography;

function formatUtc(value?: string): string {
  if (!value) return '—';
  const parsed = dayjs(value);
  return parsed.isValid() ? parsed.format('YYYY-MM-DD HH:mm:ss') : value;
}

export interface TaskDetailsDrawerProps {
  open: boolean;
  workspaceId: string;
  task: TaskDto | null;
  events: TaskEventWatchEvent[];
  onClose: () => void;
  onEdit: (task: TaskDto) => void;
  onCommand: (task: TaskDto, command: TaskCommandWire) => void;
  onDeleted: (taskId: string) => void;
}

/** 详情（全字段 + 绑定 ID）+ 事件时间线 + 执行链接 + 危险区（硬删）。 */
export const TaskDetailsDrawer: React.FC<TaskDetailsDrawerProps> = ({
  open,
  workspaceId,
  task,
  events,
  onClose,
  onEdit,
  onCommand,
  onDeleted,
}) => {
  const { modal, message } = App.useApp();

  const handleDelete = () => {
    if (!task) return;
    modal.confirm({
      title: '确认永久删除该任务？',
      content: '仅无历史 Backlog 任务可硬删；删除后不可恢复。',
      okText: '删除',
      okType: 'danger',
      cancelText: '取消',
      onOk: async () => {
        try {
          await deleteTask(workspaceId, task.taskId);
          message.success('已删除');
          onDeleted(task.taskId);
          onClose();
        } catch (error) {
          message.error(parseTaskError(error).body?.message ?? '删除失败');
        }
      },
    });
  };

  return (
    <Drawer
      title={task ? `任务详情：${task.title}` : '任务详情'}
      open={open}
      onClose={onClose}
      width={560}
      extra={
        task ? (
          <Space>
            {task.status === 'Failed' && (
              <Button type="primary" onClick={() => onCommand(task, 'Reopen')}>
                重新打开
              </Button>
            )}
            <Button onClick={() => onEdit(task)}>编辑</Button>
          </Space>
        ) : null
      }
    >
      {!task ? (
        <Text type="secondary">未选择任务</Text>
      ) : (
        <>
          <Descriptions bordered size="small" column={1}>
            <Descriptions.Item label="任务 ID">{task.taskId}</Descriptions.Item>
            <Descriptions.Item label="状态">
              {TASK_STATUS_LABELS[task.status]}（{task.status}）
            </Descriptions.Item>
            <Descriptions.Item label="看板列">{task.boardColumn}</Descriptions.Item>
            <Descriptions.Item label="优先级">
              <Tag>{TASK_PRIORITY_LABELS[task.priority]}</Tag>
            </Descriptions.Item>
            <Descriptions.Item label="执行窗口">
              {TASK_EXECUTION_WINDOW_LABELS[task.executionWindow]}
            </Descriptions.Item>
            <Descriptions.Item label="描述">
              {task.description ?? '—'}
            </Descriptions.Item>
            <Descriptions.Item label="验收标准">
              {task.acceptanceCriteria ?? '—'}
            </Descriptions.Item>
            <Descriptions.Item label="偏好 Agent">
              {task.preferredAgentId ?? '—'}
            </Descriptions.Item>
            <Descriptions.Item label="活跃 Assignment">
              <TaskExecutionLink task={task} />
            </Descriptions.Item>
            <Descriptions.Item label="最早可执行">
              {formatUtc(task.notBeforeUtc)}
            </Descriptions.Item>
            <Descriptions.Item label="截止">
              {formatUtc(task.dueAtUtc)}
            </Descriptions.Item>
            <Descriptions.Item label="下次可派发">
              {formatUtc(task.nextEligibleAtUtc)}
            </Descriptions.Item>
            <Descriptions.Item label="排序序号">{task.sortOrder}</Descriptions.Item>
            <Descriptions.Item label="进度">
              {typeof task.progressPercent === 'number'
                ? `${task.progressPercent}%${task.progressSummary ? ` · ${task.progressSummary}` : ''}`
                : '—'}
            </Descriptions.Item>
            {task.status === 'Blocked' && (
              <Descriptions.Item label="阻塞">
                {task.blockerKind ?? ''} {task.blockerReason ?? ''}
              </Descriptions.Item>
            )}
            {task.status === 'Failed' && (
              <Descriptions.Item label="失败">
                {task.failureCode ?? ''} {task.failureReason ?? ''}
              </Descriptions.Item>
            )}
            <Descriptions.Item label="版本">{task.version}</Descriptions.Item>
            <Descriptions.Item label="创建者">{task.createdBy ?? '—'}</Descriptions.Item>
            <Descriptions.Item label="更新者">{task.updatedBy ?? '—'}</Descriptions.Item>
            <Descriptions.Item label="创建时间">
              {formatUtc(task.createdAtUtc)}
            </Descriptions.Item>
            <Descriptions.Item label="更新时间">
              {formatUtc(task.updatedAtUtc)}
            </Descriptions.Item>
            <Descriptions.Item label="完成时间">
              {formatUtc(task.completedAtUtc)}
            </Descriptions.Item>
            <Descriptions.Item label="失败时间">
              {formatUtc(task.failedAtUtc)}
            </Descriptions.Item>
            <Descriptions.Item label="归档时间">
              {formatUtc(task.archivedAtUtc)}
            </Descriptions.Item>
          </Descriptions>

          <div style={{ marginTop: 16 }}>
            <TaskEventTimeline events={events} />
          </div>

          {task.status === 'Backlog' && (
            <div style={{ marginTop: 16 }}>
              <Text type="danger" strong>
                危险区
              </Text>
              <div style={{ marginTop: 8 }}>
                <Button danger onClick={handleDelete}>
                  永久删除
                </Button>
              </div>
            </div>
          )}
        </>
      )}
    </Drawer>
  );
};

export default TaskDetailsDrawer;
