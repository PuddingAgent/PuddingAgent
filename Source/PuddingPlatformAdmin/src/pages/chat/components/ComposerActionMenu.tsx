// ── ComposerActionMenu：`+` 菜单，只展示输入动作与会话动作 ──
import { DownloadOutlined, PaperClipOutlined, PictureOutlined, SettingOutlined } from '@ant-design/icons';
import { Tooltip } from 'antd';
import React from 'react';
import { useChatStyles } from '../styles';

interface ComposerActionMenuProps {
  onExport: () => void;
  onClose: () => void;
}

const ComposerActionMenu: React.FC<ComposerActionMenuProps> = ({ onExport, onClose }) => {
  const { styles } = useChatStyles();

  return (
    <div className={styles.composerMenu}>
      {/* 添加到本轮 */}
      <div className={styles.composerMenuSection}>
        <div className={styles.composerMenuSectionTitle}>添加到本轮</div>
        <Tooltip title="即将开放">
          <button
            className={styles.composerMenuItem + ' ' + styles.composerMenuItemDisabled}
            disabled
            aria-label="上传附件，即将开放"
          >
            <PaperClipOutlined />
            <span>附件</span>
            <span className={styles.composerMenuComingSoon}>即将开放</span>
          </button>
        </Tooltip>
        <Tooltip title="即将开放">
          <button
            className={styles.composerMenuItem + ' ' + styles.composerMenuItemDisabled}
            disabled
            aria-label="上传图片，即将开放"
          >
            <PictureOutlined />
            <span>图片</span>
            <span className={styles.composerMenuComingSoon}>即将开放</span>
          </button>
        </Tooltip>
      </div>

      {/* 本轮设置 */}
      <div className={styles.composerMenuSection}>
        <div className={styles.composerMenuSectionTitle}>本轮设置</div>
        <Tooltip title="即将开放">
          <button
            className={styles.composerMenuItem + ' ' + styles.composerMenuItemDisabled}
            disabled
            aria-label="思考强度，即将开放"
          >
            <SettingOutlined />
            <span>思考强度</span>
            <span className={styles.composerMenuValue}>自动</span>
          </button>
        </Tooltip>
      </div>

      {/* 会话 */}
      <div className={styles.composerMenuSection}>
        <div className={styles.composerMenuSectionTitle}>会话</div>
        <button
          className={styles.composerMenuItem}
          onClick={() => { onExport(); onClose(); }}
          aria-label="导出对话"
        >
          <DownloadOutlined />
          <span>导出对话</span>
        </button>
      </div>
    </div>
  );
};

export default ComposerActionMenu;
