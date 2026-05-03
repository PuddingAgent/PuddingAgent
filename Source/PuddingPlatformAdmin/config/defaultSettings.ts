import type { ProLayoutProps } from '@ant-design/pro-components';

/**
 * @name Pudding Platform 管理后台配置
 */
const Settings: ProLayoutProps & {
  pwa?: boolean;
  logo?: string;
} = {
  navTheme: 'light',
  // Pudding 主色 — 靛蓝
  colorPrimary: '#6366f1',
  layout: 'mix',
  contentWidth: 'Fluid',
  fixedHeader: true,
  fixSiderbar: true,
  colorWeak: false,
  title: 'Pudding Platform',
  pwa: false,
  logo: '/admin/assets/images/logo.png',
  iconfontUrl: '',
  token: {},
};

export default Settings;
