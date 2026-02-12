import routes from '../../config/routes';
import zhCNMenu from '../locales/zh-CN/menu';

function findRoute(path: string, list: any[] = routes): any | undefined {
  for (const route of list) {
    if (route.path === path) return route;
    const child = route.routes ? findRoute(path, route.routes) : undefined;
    if (child) return child;
  }
  return undefined;
}

function findTopLevelRoute(path: string): any | undefined {
  return routes.find((route: any) => route.path === path);
}

function visibleTopLevelPaths(): string[] {
  return routes
    .filter((route: any) => !route.hideInMenu && route.layout !== false && route.path !== '*')
    .map((route: any) => route.path);
}

describe('admin workspace menu routing', () => {
  it('keeps admin routes separate from user-facing workspace routes', () => {
    expect(findRoute('/')).toEqual(expect.objectContaining({
      path: '/',
      name: 'adminHome',
      icon: 'dashboard',
      component: './Admin',
    }));

    expect(findRoute('/workspace')).toEqual(expect.objectContaining({
      path: '/workspace',
      name: 'workspace',
      icon: 'appstore',
      redirect: '/workspace/default',
    }));

    expect(findRoute('/pudding/workspaces')).toEqual(expect.objectContaining({
      path: '/pudding/workspaces',
      layout: false,
      hideInMenu: true,
      component: './workspace',
    }));

    expect(findRoute('/pudding/workspaces/:workspaceId')).toEqual(expect.objectContaining({
      path: '/pudding/workspaces/:workspaceId',
      layout: false,
      hideInMenu: true,
      component: './workspace-studio',
    }));

    expect(findRoute('/pudding/workspaces/:workspaceId/:agentId')).toEqual(expect.objectContaining({
      path: '/pudding/workspaces/:workspaceId/:agentId',
      layout: false,
      hideInMenu: true,
      component: './workspace-studio',
    }));

    expect(findRoute('/workspace/:id/settings')).toEqual(expect.objectContaining({
      path: '/workspace/:id/settings',
      component: './workspace-settings-redirect',
      hideInMenu: true,
    }));

    expect(findRoute('/workspace/:id')).toEqual(expect.objectContaining({
      path: '/workspace/:id',
      component: './workspace/[id]',
      hideInMenu: true,
    }));
  });

  it('uses the management label expected by the admin sidebar', () => {
    expect(zhCNMenu['menu.workspace']).toBe('工作区管理');
    expect(zhCNMenu['menu.adminHome']).toBe('后台首页');
  });

  it('groups system-level account and permission pages under system config', () => {
    const systemConfigRoute = findTopLevelRoute('/system-config');

    expect(systemConfigRoute).toEqual(expect.objectContaining({
      path: '/system-config',
      name: 'systemConfig',
      icon: 'setting',
    }));

    expect(systemConfigRoute?.routes).toEqual(expect.arrayContaining([
      expect.objectContaining({ path: '/system-config/user-management', name: 'userManagement' }),
      expect.objectContaining({ path: '/system-config/team-management', name: 'teamManagement' }),
      expect.objectContaining({ path: '/system-config/role-management', name: 'roleManagement' }),
    ]));

    expect(findTopLevelRoute('/user-management')).toBeUndefined();
    expect(findTopLevelRoute('/team-management')).toBeUndefined();
    expect(findTopLevelRoute('/role-management')).toBeUndefined();
    expect(zhCNMenu['menu.systemConfig']).toBe('系统配置');
    expect(zhCNMenu['menu.systemConfig.userManagement']).toBe('用户管理');
    expect(zhCNMenu['menu.systemConfig.teamManagement']).toBe('团队管理');
    expect(zhCNMenu['menu.systemConfig.roleManagement']).toBe('权限角色');
  });

  it('groups approval governance pages under security approval', () => {
    const approvalRoute = findTopLevelRoute('/tool-approval');

    expect(approvalRoute).toEqual(expect.objectContaining({
      path: '/tool-approval',
      name: 'toolApproval',
      icon: 'safety',
    }));

    expect(approvalRoute?.routes).toEqual(expect.arrayContaining([
      expect.objectContaining({ path: '/tool-approval/allowlist', name: 'allowlist' }),
      expect.objectContaining({ path: '/tool-approval/audit', name: 'audit' }),
    ]));

    expect(zhCNMenu['menu.toolApproval']).toBe('安全审批');
    expect(zhCNMenu['menu.toolApproval.allowlist']).toBe('审批白名单');
    expect(zhCNMenu['menu.toolApproval.audit']).toBe('审批审计');
  });

  it('keeps system config as the last visible top-level menu item', () => {
    const paths = visibleTopLevelPaths();

    expect(paths.at(-1)).toBe('/system-config');
  });
});
