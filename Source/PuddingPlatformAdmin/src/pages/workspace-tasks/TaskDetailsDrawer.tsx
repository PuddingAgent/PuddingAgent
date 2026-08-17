import {
  App,
  Button,
  Descriptions,
  Drawer,
  Empty,
  Input,
  List,
  Select,
  Space,
  Tag,
  Typography,
} from 'antd';
import React, { useEffect, useState } from 'react';
import dayjs from 'dayjs';
import {
  createTaskComment,
  deleteTask,
  listTaskComments,
  updateTask,
} from '@/services/platform/api';
import { TaskEventTimeline } from './TaskEventTimeline';
import { TaskExecutionLink } from './TaskExecutionLink';
import {
  parseTaskError,
  TASK_EXECUTION_WINDOW_LABELS,
  TASK_PRIORITY_LABELS,
  TASK_STATUS_LABELS,
  type TaskCommandWire,
  type TaskCommentDto,
  type TaskDto,
  type TaskEventWatchEvent,
  type TaskStatusWire,
} from './types';

const { Text } = Typography;
const { TextArea } = Input;

function formatUtc(value?: string): string {
  if (!value) return '—';
  const parsed = dayjs(value);
  return parsed.isValid() ? parsed.format('YYYY-MM-DD HH:mm:ss') : value;
}

function authorKindLabel(kind: string): string {
  switch (kind) {
    case 'agent':
      return 'Agent';
    case 'system':
      return '系统';
    default:
      return '用户';
  }
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
  /** 状态流转成功后回调（携带 updateTask 返回的最新 TaskDto，供父层刷新列与选中态）。 */
  onChanged: (task: TaskDto) => void;
}

/** 详情（全字段 + 绑定 ID）+ 状态流转 + 评论/备注 + 事件时间线 + 执行链接 + 危险区（硬删）。 */
export const TaskDetailsDrawer: React.FC<TaskDetailsDrawerProps> = ({
  open,
  workspaceId,
  task,
  events,
  onClose,
  onEdit,
  onCommand,
  onDeleted,
  onChanged,
}) => {
  const { modal, message } = App.useApp();

  const [transitionTarget, setTransitionTarget] = useState<
    TaskStatusWire | undefined
  >(undefined);
  const [transitionNote, setTransitionNote] = useState('');
  const [transitioning, setTransitioning] = useState(false);
  const [comments, setComments] = useState<TaskCommentDto[]>([]);
  const [commentText, setCommentText] = useState('');
  const [commentSubmitting, setCommentSubmitting] = useState(false);

  // 打开/切换任务时重置流转表单并拉取评论（fail-open：拉取失败不阻断详情展示）。
  useEffect(() => {
    setTransitionTarget(undefined);
    setTransitionNote('');
    setCommentText('');
    setComments([]);
    if (!open || !task) return;
    let active = true;
    (async () => {
      try {
        const list = await listTaskComments(workspaceId, task.taskId);
        if (active) setComments(list);
      } catch {
        if (active) setComments([]);
      }
    })();
    return () => {
      active = false;
    };
  }, [open, task, workspaceId]);

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

  // 状态流转：只消费 allowedTransitions，不实现任何状态机（ADR-073 §6 不变量）。
  const handleTransition = async () => {
    if (!task || !transitionTarget) return;
    const from = task.status;
    const to = transitionTarget;
    const note = transitionNote.trim();
    setTransitioning(true);
    try {
      const updated = await updateTask(workspaceId, task.taskId, {
        expectedVersion: task.version,
        status: to,
      });
      if (note) {
        await createTaskComment(workspaceId, task.taskId, {
          content: `状态 ${from}→${to}：${note}`,
          authorKind: 'user',
        });
      }
      message.success('状态已更新');
      onChanged(updated);
      setTransitionTarget(undefined);
      setTransitionNote('');
    } catch (error) {
      const parsed = parseTaskError(error);
      if (parsed.body?.code === 'task.version_conflict') {
        message.error('任务已被他人更新，请刷新后重试');
      } else {
        message.error(parsed.body?.message ?? '流转失败');
      }
    } finally {
      setTransitioning(false);
    }
  };

  const handleAddComment = async () => {
    if (!task) return;
    const content = commentText.trim();
    if (!content) return;
    setCommentSubmitting(true);
    try {
      const created = await createTaskComment(workspaceId, task.taskId, {
        content,
        authorKind: 'user',
      });
      setComments((prev) => [...prev, created]);
      setCommentText('');
      message.success('备注已添加');
    } catch (error) {
      message.error(parseTaskError(error).body?.message ?? '添加备注失败');
    } finally {
      setCommentSubmitting(false);
    }
  };

  const transitionOptions = task
    ? task.allowedTransitions.map((status) => ({
        value: status,
        label: TASK_STATUS_LABELS[status],
      }))
    : [];

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

          {/* 状态流转（F-3）：只消费 allowedTransitions，空列表 fail-closed */}
          <div style={{ marginTop: 16 }}>
            <Text strong>状态流转</Text>
            <div style={{ marginTop: 8 }}>
              <Space direction="vertical" style={{ width: '100%' }} size={8}>
                <Text type="secondary" style={{ fontSize: 12 }}>
                  当前状态：{TASK_STATUS_LABELS[task.status]}（{task.status}）
                </Text>
                {task.allowedTransitions.length === 0 ? (
                  <Text type="secondary" style={{ fontSize: 12 }}>
                    无可迁移状态
                  </Text>
                ) : (
                  <Select
                    style={{ width: '100%' }}
                    placeholder="选择目标状态"
                    value={transitionTarget}
                    onChange={(value) =>
                      setTransitionTarget(value as TaskStatusWire)
                    }
                    options={transitionOptions}
                  />
                )}
                <TextArea
                  rows={2}
                  placeholder="流转备注（可选）"
                  value={transitionNote}
                  onChange={(e) => setTransitionNote(e.target.value)}
                />
                <Button
                  type="primary"
                  loading={transitioning}
                  disabled={!transitionTarget || task.allowedTransitions.length === 0}
                  onClick={handleTransition}
                  data-testid="transition-submit"
                >流转</Button>
              </Space>
            </div>
          </div>

          <div style={{ marginTop: 16 }}>
            <TaskEventTimeline events={events} />
          </div>

          {/* 评论/备注（F-4） */}
          <div style={{ marginTop: 16 }}>
            <Text strong>评论/备注</Text>
            <div style={{ marginTop: 8 }}>
              {comments.length === 0 ? (
                <Empty
                  image={Empty.PRESENTED_IMAGE_SIMPLE}
                  description="暂无备注"
                />
              ) : (
                <List
                  size="small"
                  dataSource={comments}
                  renderItem={(comment) => (
                    <List.Item>
                      <div style={{ width: '100%' }}>
                        <Space size={8}>
                          <Tag>{authorKindLabel(comment.authorKind)}</Tag>
                          {comment.authorId && (
                            <Text type="secondary" style={{ fontSize: 12 }}>
                              {comment.authorId}
                            </Text>
                          )}
                        </Space>
                        <div>
                          <Text type="secondary" style={{ fontSize: 12 }}>
                            {formatUtc(comment.createdAtUtc)}
                          </Text>
                        </div>
                        <div style={{ marginTop: 4, whiteSpace: 'pre-wrap' }}>
                          {comment.content}
                        </div>
                      </div>
                    </List.Item>
                  )}
                />
              )}
              <Space.Compact style={{ width: '100%', marginTop: 8 }}>
                <Input
                  placeholder="添加备注…"
                  value={commentText}
                  onChange={(e) => setCommentText(e.target.value)}
                  onPressEnter={handleAddComment}
                />
                <Button
                  type="primary"
                  loading={commentSubmitting}
                  disabled={!commentText.trim()}
                  onClick={handleAddComment}
                  data-testid="comment-submit"
                >添加备注</Button>
              </Space.Compact>
            </div>
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
