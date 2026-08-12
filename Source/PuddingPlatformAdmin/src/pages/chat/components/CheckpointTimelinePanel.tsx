// ── CheckpointTimelinePanel (P2#7) ────────────────────────────
// 时间线面板：展示当前会话的 Checkpoint 快照列表，
// 每条支持 Restore（还原到快照）与 Fork（从快照分支新会话）。
import { DeleteOutlined, ForkOutlined, RollbackOutlined } from '@ant-design/icons';
import { Button, Empty, Tooltip, Typography } from 'antd';
import React, { useMemo } from 'react';
import type { ChatCheckpoint } from '../client/checkpointStore';
import { useChatStyles } from '../styles';

interface CheckpointTimelinePanelProps {
  open: boolean;
  sessionId: string | null;
  checkpoints: ChatCheckpoint[];
  restoredCheckpointId: string | null;
  formatTime: (timestamp: number) => string;
  onRestore: (checkpointId: string) => void;
  onFork: (checkpointId: string) => void;
  onDelete: (checkpointId: string) => void;
  onClearAll: () => void;
  onClose: () => void;
  forkLoading?: boolean;
}

/**
 * 轻量时间线卡片列表（内嵌于消息区右侧抽屉或独立面板）。
 * 组件只做展示与事件转发，不持有任何状态。
 */
const CheckpointTimelinePanel: React.FC<CheckpointTimelinePanelProps> = ({
  open,
  sessionId,
  checkpoints,
  restoredCheckpointId,
  formatTime,
  onRestore,
  onFork,
  onDelete,
  onClearAll,
  onClose,
  forkLoading = false,
}) => {
  const { styles } = useChatStyles();

  const sorted = useMemo(
    () =>
      [...checkpoints].sort(
        (left, right) => right.createdAt - left.createdAt,
      ),
    [checkpoints],
  );

  if (!open) return null;
  if (!sessionId) {
    return (
      <div className={styles.checkpointPanel} data-testid="checkpoint-panel">
        <Empty
          image={Empty.PRESENTED_IMAGE_SIMPLE}
          description="请先选择会话"
          data-testid="checkpoint-empty"
        />
      </div>
    );
  }

  return (
    <div className={styles.checkpointPanel} data-testid="checkpoint-panel">
      <div className={styles.checkpointPanelHeader}>
        <Typography.Text strong data-testid="checkpoint-panel-title">
          Checkpoint 时间线
        </Typography.Text>
        <Tooltip title="清空全部快照">
          <Button
            type="text"
            size="small"
            icon={<DeleteOutlined />}
            aria-label="清空全部快照"
            disabled={sorted.length === 0}
            onClick={onClearAll}
            data-testid="checkpoint-clear-all"
          />
        </Tooltip>
        <Tooltip title="关闭">
          <Button
            type="text"
            size="small"
            aria-label="关闭时间线"
            onClick={onClose}
            data-testid="checkpoint-close"
          >
            ✕
          </Button>
        </Tooltip>
      </div>

      {sorted.length === 0 ? (
        <Empty
          image={Empty.PRESENTED_IMAGE_SIMPLE}
          description="每次发送消息前会自动保存快照"
          data-testid="checkpoint-empty"
        />
      ) : (
        <ul className={styles.checkpointList} data-testid="checkpoint-list">
          {sorted.map((checkpoint) => {
            const isRestored =
              restoredCheckpointId === checkpoint.checkpointId;
            return (
              <li
                key={checkpoint.checkpointId}
                className={
                  isRestored
                    ? styles.checkpointItemRestored
                    : styles.checkpointItem
                }
                data-testid={`checkpoint-item-${checkpoint.checkpointId}`}
                data-restored={isRestored ? 'true' : 'false'}
              >
                <div className={styles.checkpointItemHeader}>
                  <span
                    className={styles.checkpointItemTime}
                    data-testid="checkpoint-item-time"
                  >
                    {formatTime(checkpoint.createdAt)}
                  </span>
                  <span
                    className={styles.checkpointItemCount}
                    data-testid="checkpoint-item-count"
                  >
                    {checkpoint.turnIndex} 轮
                  </span>
                </div>
                <div
                  className={styles.checkpointItemLabel}
                  data-testid="checkpoint-item-label"
                  title={checkpoint.label}
                >
                  {checkpoint.label || '（空会话）'}
                </div>
                <div className={styles.checkpointItemActions}>
                  <Button
                    type="link"
                    size="small"
                    icon={<RollbackOutlined />}
                    aria-label={`还原到快照 ${checkpoint.label}`}
                    disabled={isRestored}
                    onClick={() => onRestore(checkpoint.checkpointId)}
                    data-testid={`checkpoint-restore-${checkpoint.checkpointId}`}
                  >
                    {isRestored ? '已还原' : 'Restore'}
                  </Button>
                  <Button
                    type="link"
                    size="small"
                    icon={<ForkOutlined />}
                    aria-label={`从快照分支 ${checkpoint.label}`}
                    loading={forkLoading}
                    onClick={() => onFork(checkpoint.checkpointId)}
                    data-testid={`checkpoint-fork-${checkpoint.checkpointId}`}
                  >
                    Fork
                  </Button>
                  <Tooltip title="删除该快照">
                    <Button
                      type="text"
                      size="small"
                      icon={<DeleteOutlined />}
                      aria-label="删除快照"
                      onClick={() => onDelete(checkpoint.checkpointId)}
                      data-testid={`checkpoint-delete-${checkpoint.checkpointId}`}
                    />
                  </Tooltip>
                </div>
              </li>
            );
          })}
        </ul>
      )}
    </div>
  );
};

export default CheckpointTimelinePanel;
