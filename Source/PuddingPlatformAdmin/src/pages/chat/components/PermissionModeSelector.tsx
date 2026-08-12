// ── PermissionModeSelector：权限模式选择器（P1#4）────────────
// 四档：Manual（每步需批）/ acceptEdits（只批编辑）/ plan（先计划后执行）/ auto（自动执行）
// 状态由 useChatState 全局持有，经 ChatLayout → ChatMain → Composer 下传。
import {
  DownOutlined,
  EditOutlined,
  LockOutlined,
  ProfileOutlined,
  SafetyOutlined,
  ThunderboltOutlined,
} from '@ant-design/icons';
import { Popover } from 'antd';
import React from 'react';
import { useChatStyles } from '../styles';
import {
  PERMISSION_MODE_LABELS,
  PERMISSION_MODES,
  type PermissionMode,
} from '../types/chatStateTypes';

interface PermissionModeSelectorProps {
  /** 当前权限模式 */
  value: PermissionMode;
  /** 模式切换回调 */
  onChange: (mode: PermissionMode) => void;
  /** 是否禁用（如正在生成/未选择工作空间） */
  disabled?: boolean;
}

const PERMISSION_MODE_ICONS: Record<PermissionMode, React.ReactNode> = {
  manual: <LockOutlined />,
  acceptEdits: <EditOutlined />,
  plan: <ProfileOutlined />,
  auto: <ThunderboltOutlined />,
};

const PERMISSION_MODE_DESCRIPTIONS: Record<PermissionMode, string> = {
  manual: '每步工具调用都需确认',
  acceptEdits: '仅文件编辑需确认',
  plan: '先展示计划，确认后执行',
  auto: '全程自动，不打断',
};

const PermissionModeSelector: React.FC<PermissionModeSelectorProps> = ({
  value,
  onChange,
  disabled = false,
}) => {
  const { styles } = useChatStyles();
  const [open, setOpen] = React.useState(false);

  const handleSelect = (mode: PermissionMode) => {
    onChange(mode);
    setOpen(false);
  };

  return (
    <Popover
      trigger="click"
      placement="topRight"
      open={open}
      onOpenChange={(next) => {
        if (!disabled) setOpen(next);
      }}
      content={
        <div
          className={styles.composerPermissionModeMenu}
          role="listbox"
          aria-label="权限模式"
          data-testid="permission-mode-menu"
        >
          {PERMISSION_MODES.map((mode) => (
            <button
              key={mode}
              type="button"
              role="option"
              aria-selected={value === mode}
              className={styles.composerPermissionModeItem}
              data-active={value === mode ? 'true' : undefined}
              data-testid={`permission-mode-option-${mode}`}
              onClick={() => handleSelect(mode)}
            >
              <span className={styles.composerPermissionModeItemIcon}>
                {PERMISSION_MODE_ICONS[mode]}
              </span>
              <span className={styles.composerPermissionModeItemText}>
                <span className={styles.composerPermissionModeItemLabel}>
                  {PERMISSION_MODE_LABELS[mode]}
                </span>
                <span className={styles.composerPermissionModeItemDesc}>
                  {PERMISSION_MODE_DESCRIPTIONS[mode]}
                </span>
              </span>
            </button>
          ))}
        </div>
      }
    >
      <button
        type="button"
        className={styles.composerPermissionModeButton}
        aria-label={`权限模式：${PERMISSION_MODE_LABELS[value]}`}
        aria-haspopup="listbox"
        aria-expanded={open}
        disabled={disabled}
        data-testid="permission-mode-selector"
      >
        <SafetyOutlined />
        <span>{PERMISSION_MODE_LABELS[value]}</span>
        <DownOutlined />
      </button>
    </Popover>
  );
};

export default React.memo(PermissionModeSelector);
