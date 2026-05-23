// ── MessageActions：消息操作按钮组 ─────────────────────────
import { CopyOutlined, DeleteOutlined, PushpinOutlined, ReloadOutlined } from '@ant-design/icons';
import { Tooltip } from 'antd';
import React from 'react';
import { useChatStyles } from '../styles';

interface MessageActionsProps {
  content: string;
  visible: boolean;
  onCopy?: () => void;
  onRerun?: () => void;
  onPin?: () => void;
  onDelete?: () => void;
}

const MessageActions: React.FC<MessageActionsProps> = ({
  content,
  visible,
  onCopy,
  onRerun,
  onPin,
  onDelete,
}) => {
  const { styles, cx } = useChatStyles();

  return (
    <div
      className={cx(styles.messageActionsNew, visible && styles.messageActionsVisible)}
      onClick={(e) => e.stopPropagation()}
    >
      {onCopy && (
        <Tooltip title="复制">
          <button
            className={styles.messageActionBtn}
            onClick={() => { navigator.clipboard.writeText(content).catch(() => {}); onCopy?.(); }}
            aria-label="复制"
          >
            <CopyOutlined />
          </button>
        </Tooltip>
      )}
      {onRerun && (
        <Tooltip title="重新生成">
          <button
            className={styles.messageActionBtn}
            onClick={onRerun}
            aria-label="重新生成"
          >
            <ReloadOutlined />
          </button>
        </Tooltip>
      )}
      {onPin && (
        <Tooltip title="固定">
          <button
            className={styles.messageActionBtn}
            onClick={onPin}
            aria-label="固定"
          >
            <PushpinOutlined />
          </button>
        </Tooltip>
      )}
      {onDelete && (
        <Tooltip title="删除">
          <button
            className={`${styles.messageActionBtn} ${styles.messageActionBtnDanger}`}
            onClick={onDelete}
            aria-label="删除"
          >
            <DeleteOutlined />
          </button>
        </Tooltip>
      )}
    </div>
  );
};

export default React.memo(MessageActions);
