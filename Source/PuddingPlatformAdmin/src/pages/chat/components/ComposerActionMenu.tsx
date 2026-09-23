// ── ComposerActionMenu：`+` 菜单，只展示输入动作与会话动作 ──
import {
  CameraOutlined,
  DownloadOutlined,
  PaperClipOutlined,
  PictureOutlined,
  RightOutlined,
  SettingOutlined,
  ThunderboltOutlined,
} from '@ant-design/icons';
import React from 'react';
import { useChatStyles } from '../styles';

interface ComposerActionMenuProps {
  onExport: () => void;
  /** 打开技能子面板（级联 flyout；hover 与点击均触发）。 */
  onOpenSkills?: () => void;
  /** 鼠标悬停「技能」项时展开右侧子面板。 */
  onHoverSkills?: () => void;
  /** 技能子面板已展开：该项保持高亮（否则 hover 移向子面板后高亮会断）。 */
  skillsActive?: boolean;
  onOpenCamera?: () => void;
  cameraEnabled?: boolean;
  onOpenImage?: () => void;
  imageEnabled?: boolean;
  onClose: () => void;
}

const ComposerActionMenu: React.FC<ComposerActionMenuProps> = ({
  onExport,
  onOpenSkills,
  onHoverSkills,
  skillsActive = false,
  onOpenCamera,
  cameraEnabled = false,
  onOpenImage,
  imageEnabled = false,
  onClose,
}) => {
  const { styles } = useChatStyles();

  return (
    <div className={styles.composerMenu}>
      {/* 添加到本轮 */}
      <div className={styles.composerMenuSection}>
        <div className={styles.composerMenuSectionTitle}>添加到本轮</div>
        <button
          className={
            styles.composerMenuItem + ' ' + styles.composerMenuItemDisabled
          }
          disabled
          title="即将开放"
          aria-label="上传附件，即将开放"
        >
          <PaperClipOutlined />
          <span>附件</span>
          <span className={styles.composerMenuComingSoon}>即将开放</span>
        </button>
        <button
          className={
            styles.composerMenuItem +
            (cameraEnabled ? '' : ' ' + styles.composerMenuItemDisabled)
          }
          disabled={!cameraEnabled}
          title={
            cameraEnabled
              ? '打开摄像头并发送视觉请求'
              : '请选择工作空间和 Agent 后使用'
          }
          aria-label={
            cameraEnabled ? '打开摄像头视觉输入' : '摄像头视觉输入不可用'
          }
          onClick={() => {
            if (!cameraEnabled) return;
            onOpenCamera?.();
            onClose();
          }}
        >
          <CameraOutlined />
          <span>摄像头</span>
        </button>
                <button
          className={
            styles.composerMenuItem +
            (imageEnabled ? '' : ' ' + styles.composerMenuItemDisabled)
          }
          disabled={!imageEnabled}
          title={
            imageEnabled
              ? '上传图片并发送视觉请求'
              : '请选择工作空间和 Agent 后使用'
          }
          aria-label={imageEnabled ? '上传图片视觉输入' : '图片视觉输入不可用'}
          onClick={() => {
            if (!imageEnabled) return;
            onOpenImage?.();
            onClose();
          }}
        >
          <PictureOutlined />
          <span>图片</span>
        </button>
        {/* 技能：hover 时在右侧**并排**展开子面板（级联菜单，参照 WorkBuddy），
            而非把本菜单内容换成面板 —— 主菜单始终可见，不靠「返回」回退。
            选中后转换为文本附加到本轮（不经过发送协议参数）。 */}
        <button
          className={
            styles.composerMenuItem +
            (onOpenSkills ? '' : ' ' + styles.composerMenuItemDisabled)
          }
          disabled={!onOpenSkills}
          style={
            skillsActive
              ? {
                  background:
                    'color-mix(in srgb, var(--earth-brown, #5c4a3a) 8%, transparent)',
                }
              : undefined
          }
          title={onOpenSkills ? '选择技能并附加到本轮' : '技能不可用'}
          aria-label={onOpenSkills ? '选择技能' : '技能不可用'}
          aria-haspopup="true"
          aria-expanded={skillsActive}
          onMouseEnter={() => onHoverSkills?.()}
          onFocus={() => onHoverSkills?.()}
          onClick={() => {
            if (!onOpenSkills) return;
            onOpenSkills();
          }}
        >
          <ThunderboltOutlined />
          <span>技能</span>
          {/* 只有本项有下级，其余项没有子面板 —— 不给出误导性的箭头。 */}
          <RightOutlined
            style={{ marginLeft: 'auto', fontSize: 10, opacity: 0.55 }}
          />
        </button>
      </div>

      {/* 本轮设置 */}
      <div className={styles.composerMenuSection}>
        <div className={styles.composerMenuSectionTitle}>本轮设置</div>
        <button
          className={
            styles.composerMenuItem + ' ' + styles.composerMenuItemDisabled
          }
          disabled
          title="即将开放"
          aria-label="思考强度，即将开放"
        >
          <SettingOutlined />
          <span>思考强度</span>
          <span className={styles.composerMenuValue}>自动</span>
        </button>
      </div>

      {/* 会话 */}
      <div className={styles.composerMenuSection}>
        <div className={styles.composerMenuSectionTitle}>会话</div>
        <button
          className={styles.composerMenuItem}
          onClick={() => {
            onExport();
            onClose();
          }}
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
