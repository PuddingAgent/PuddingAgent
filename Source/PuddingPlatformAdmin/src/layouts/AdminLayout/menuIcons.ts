import {
  AppstoreOutlined,
  AudioOutlined,
  AuditOutlined,
  BarChartOutlined,
  BranchesOutlined,
  BugOutlined,
  CheckCircleOutlined,
  CloudServerOutlined,
  ClusterOutlined,
  CodeOutlined,
  DatabaseOutlined,
  HomeOutlined,
  LockOutlined,
  MessageOutlined,
  ProjectOutlined,
  RobotOutlined,
  SafetyCertificateOutlined,
  SettingOutlined,
  ThunderboltOutlined,
  ToolOutlined,
  UserOutlined,
} from '@ant-design/icons';
import React from 'react';

type AdminMenuIconComponent = React.ComponentType;

const adminMenuIconComponents: Record<string, AdminMenuIconComponent> = {
  appstore: AppstoreOutlined,
  audio: AudioOutlined,
  audit: AuditOutlined,
  barChart: BarChartOutlined,
  branches: BranchesOutlined,
  bug: BugOutlined,
  checkCircle: CheckCircleOutlined,
  cloudServer: CloudServerOutlined,
  cluster: ClusterOutlined,
  code: CodeOutlined,
  database: DatabaseOutlined,
  home: HomeOutlined,
  lock: LockOutlined,
  message: MessageOutlined,
  project: ProjectOutlined,
  robot: RobotOutlined,
  safety: SafetyCertificateOutlined,
  setting: SettingOutlined,
  thunderbolt: ThunderboltOutlined,
  tool: ToolOutlined,
  user: UserOutlined,
};

export interface AdminMenuRouteConfig {
  icon?: React.ReactNode;
  path?: string;
  routes?: AdminMenuRouteConfig[];
  [key: string]: unknown;
}

/**
 * Umi 的全局 layout 插件关闭后，不再有人把 routes.ts 中的图标名转换为组件。
 * 该转换只在异步 AdminLayout 边界执行，避免把整套管理菜单图标带回 Chat 主包。
 */
export function resolveAdminMenuRoutes(
  routes: readonly AdminMenuRouteConfig[],
): AdminMenuRouteConfig[] {
  return routes.map((route) => {
    const Icon = typeof route.icon === 'string'
      ? adminMenuIconComponents[route.icon]
      : undefined;

    return {
      ...route,
      icon: Icon ? React.createElement(Icon) : route.icon,
      routes: route.routes ? resolveAdminMenuRoutes(route.routes) : undefined,
    };
  });
}
