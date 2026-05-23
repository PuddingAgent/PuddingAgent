/**
 * 这个文件作为组件的目录
 * 目的是统一管理对外输出的组件，方便分类
 */
/**
 * 布局组件
 */
import Footer from './Footer';
import { Question, SelectLang } from './RightContent';
import { AvatarDropdown, AvatarName } from './RightContent/AvatarDropdown';
import { PuddingGlobalActions } from './GlobalActions';

export { AvatarDropdown, AvatarName, Footer, PuddingGlobalActions, Question, SelectLang };

// Pudding Admin Wrapper 组件
export { PuddingAdminShell } from './PuddingAdminShell';
export { PuddingPageHeader } from './PuddingPageHeader';
export { PuddingToolbar } from './PuddingToolbar';
export { PuddingDataTable } from './PuddingDataTable';
export { PuddingStatusBadge } from './PuddingStatusBadge';
export { PuddingEntityCard } from './PuddingEntityCard';
export type { PuddingStatusTone } from './PuddingStatusBadge';
export type { PuddingPageHeaderProps } from './PuddingPageHeader';
export type { PuddingToolbarProps } from './PuddingToolbar';
export type { PuddingDataTableProps } from './PuddingDataTable';
export type { PuddingStatusBadgeProps } from './PuddingStatusBadge';
export type { PuddingEntityCardProps } from './PuddingEntityCard';
