/**
 * 这个文件作为组件的目录
 * 目的是统一管理对外输出的组件，方便分类
 */
/**
 * 布局组件
 */
import Footer from './Footer';
import { PuddingGlobalActions } from './GlobalActions';
import { Question, SelectLang } from './RightContent';
import { AvatarDropdown, AvatarName } from './RightContent/AvatarDropdown';
import UserAvatarUpload from './UserAvatarUpload';
import WorkspaceNavigationHeader from './WorkspaceNavigationHeader';

// Pudding Admin Wrapper 组件
export { PuddingAdminShell } from './PuddingAdminShell';
export type { PuddingDataTableProps } from './PuddingDataTable';
export { PuddingDataTable } from './PuddingDataTable';
export type { PuddingEntityCardProps } from './PuddingEntityCard';
export { PuddingEntityCard } from './PuddingEntityCard';
export type { PuddingPageHeaderProps } from './PuddingPageHeader';
export { PuddingPageHeader } from './PuddingPageHeader';
export type {
  PuddingStatusBadgeProps,
  PuddingStatusTone,
} from './PuddingStatusBadge';
export { PuddingStatusBadge } from './PuddingStatusBadge';
export type { PuddingToolbarProps } from './PuddingToolbar';
export { PuddingToolbar } from './PuddingToolbar';
export type { UserAvatarUploadProps } from './UserAvatarUpload';
export {
  AvatarDropdown,
  AvatarName,
  Footer,
  PuddingGlobalActions,
  Question,
  SelectLang,
  UserAvatarUpload,
  WorkspaceNavigationHeader,
};
