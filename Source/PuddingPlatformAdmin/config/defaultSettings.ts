import type { ProLayoutProps } from '@ant-design/pro-components';

export const DARK_NAV_THEME: ProLayoutProps['navTheme'] = 'realDark';

/**
 * @name Pudding Platform 管理后台配置
 */
const Settings: ProLayoutProps & {
  pwa?: boolean;
  logo?: string;
} = {
  navTheme: 'light',
  // Pudding 主色 — Violet
  colorPrimary: '#7c3aed',
  layout: 'mix',
  contentWidth: 'Fluid',
  fixedHeader: true,
  fixSiderbar: true,
  colorWeak: false,
  title: 'Pudding',
  pwa: false,
  logo: '/admin/assets/images/logo.png',
  iconfontUrl: '',
  token: {},
};

export default Settings;
