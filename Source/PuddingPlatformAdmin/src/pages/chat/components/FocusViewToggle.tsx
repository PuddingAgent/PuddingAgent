// ── FocusViewToggle：Focus view 开关（P2#8）────────────────
// 对齐 Claude VS Code Focus view：开启后每个 turn 折叠为一行（隐藏工具/thinking），
// 点击行可展开查看完整内容；运行中显示当前工具。
import { Switch, Tooltip } from 'antd';
import React from 'react';
import { useChatMessageStyles } from '../styles/messageStyleContext';

interface FocusViewToggleProps {
  value: boolean;
  onChange: (value: boolean) => void;
}

const FocusViewToggle: React.FC<FocusViewToggleProps> = ({
  value,
  onChange,
}) => {
  const { styles } = useChatMessageStyles();
  return (
    <div className={styles.focusViewToggle} data-testid="focus-view-toggle">
      <Tooltip
        title={
          value
            ? '退出专注模式（显示完整消息）'
            : '专注模式：每个回合折叠为一行，点击展开'
        }
      >
        <Switch
          size="small"
          checked={value}
          onChange={onChange}
          aria-label="专注模式"
          data-testid="focus-view-toggle-switch"
        />
      </Tooltip>
      <span className={styles.focusViewToggleLabel}>专注</span>
    </div>
  );
};

export default React.memo(FocusViewToggle);
