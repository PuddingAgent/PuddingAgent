/**
 * PuddingGlobalActions — 共享全局顶栏能力层
 *
 * 由 Console（ProLayout）和 Chat（自定义 Header）同时消费。
 * 消除主题、Help、语言、用户菜单在两个壳层间的分叉。
 *
 * ProLayout 变体：通过 actionsRender/avatarProps 渲染。
 * Chat 变体：直接嵌入 ChatMain Header 的右侧区域。
 */

import { Question, SelectLang } from '@/components/RightContent';
import { AvatarDropdown } from '@/components/RightContent/AvatarDropdown';
import { ThemeToggleAction } from '@/components/ThemeMode';
import { Avatar, Space } from 'antd';
import { UserOutlined } from '@ant-design/icons';
import React from 'react';

export interface PuddingGlobalActionsProps {
  /** 布局变体：pro-layout（Console）或 chat（Chat 沉浸式 Header） */
  variant: 'pro-layout' | 'chat';
  /** setInitialState，仅 pro-layout 变体需要用于同步 navTheme */
  setInitialState?: (state: any) => void;
  /** Chat 变体是否使用紧凑模式（移动端） */
  compact?: boolean;
}

/**
 * 共享全局操作栏：主题切换、Help、语言选择、用户头像/菜单。
 *
 * - pro-layout 变体：仅输出主题/Help/语言（用户头像由 ProLayout avatarProps 渲染）
 * - chat 变体：输出全部四项，适配 Chat Header 空间
 */
export const PuddingGlobalActions: React.FC<PuddingGlobalActionsProps> = ({
  variant,
  setInitialState,
  compact,
}) => {
  const gap = compact ? 2 : 4;

  if (variant === 'pro-layout') {
    return (
      <Space size={gap}>
        <ThemeToggleAction setInitialState={setInitialState} compact={compact} />
        <Question />
        <SelectLang />
      </Space>
    );
  }

  // chat variant — 紧凑排列，适合 Chat Header 右侧空间
  return (
    <Space size={gap}>
      <ThemeToggleAction compact />
      <div style={{ lineHeight: 0 }}>
        <Question />
      </div>
      <div style={{ lineHeight: 0 }}>
        <SelectLang />
      </div>
      <AvatarDropdown dropdownTrigger={['click']} dropdownPlacement="bottomRight">
        <Avatar
          size={22}
          icon={<UserOutlined />}
          aria-label="用户菜单"
          style={{ cursor: 'pointer', flexShrink: 0 }}
        />
      </AvatarDropdown>
    </Space>
  );
};
