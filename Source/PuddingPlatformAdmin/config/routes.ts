/**
 * @name umi 的路由配置
 * @description 只支持 path,component,routes,redirect,wrappers,name,icon 的配置
 * @param path  path 只支持两种占位符配置，第一种是动态参数 :id 的形式，第二种是 * 通配符，通配符只能出现路由字符串的最后。
 * @param component 配置 location 和 path 匹配后用于渲染的 React 组件路径。可以是绝对路径，也可以是相对路径，如果是相对路径，会从 src/pages 开始找起。
 * @param routes 配置子路由，通常在需要为多个路径增加 layout 组件时使用。
 * @param redirect 配置路由跳转
 * @param wrappers 配置路由组件的包装组件，通过包装组件可以为当前的路由组件组合进更多的功能。 比如，可以用于路由级别的权限校验
 * @param name 配置路由的标题，默认读取国际化文件 menu.ts 中 menu.xxxx 的值，如配置 name 为 login，则读取 menu.ts 中 menu.login 的取值作为标题
 * @param icon 配置路由的图标，取值参考 https://ant.design/components/icon-cn， 注意去除风格后缀和大小写，如想要配置图标为 <StepBackwardOutlined /> 则取值应为 stepBackward 或 StepBackward，如想要配置图标为 <UserOutlined /> 则取值应为 user 或者 User
 * @doc https://umijs.org/docs/guides/routes
 */
export default [
  {
    path: '/user',
    layout: false,
    routes: [
      {
        name: 'login',
        path: '/user/login',
        component: './user/login',
      },
    ],
  },
  {
    path: '/welcome',
    layout: false,
    component: './workspace-entry',
    hideInMenu: true,
  },
  {
    path: '/',
    name: 'adminHome',
    icon: 'dashboard',
    component: './Admin',
  },
  {
    path: '/chat',
    name: 'chat',
    icon: 'message',
    layout: false,
    component: './chat',
  },
  {
    path: '/workspace',
    name: 'workspace',
    icon: 'appstore',
    redirect: '/workspace/default',
  },
  {
    path: '/pudding/workspaces',
    layout: false,
    hideInMenu: true,
    component: './workspace',
  },
  {
    path: '/pudding/workspaces/:workspaceId',
    layout: false,
    hideInMenu: true,
    component: './workspace-studio',
  },
  {
    path: '/pudding/workspaces/:workspaceId/:agentId',
    layout: false,
    hideInMenu: true,
    component: './workspace-studio',
  },
  {
    path: '/workspace-studio',
    layout: false,
    hideInMenu: true,
    component: './workspace-studio',
  },
  {
    path: '/workspace/:id/settings',
    component: './workspace-settings-redirect',
    hideInMenu: true,
  },
  {
    path: '/workspace/:id',
    component: './workspace/[id]',
    hideInMenu: true,
  },
  {
    path: '/workspace-agent-template',
    redirect: '/workspace/default',
    hideInMenu: true,
  },
  {
    path: '/llm-resource-pool',
    name: 'llmResourcePool',
    icon: 'thunderbolt',
    component: './llm-resource-pool',
  },
  {
    path: '/voice-models',
    name: 'voiceModels',
    icon: 'audio',
    component: './voice-models',
  },
  {
    path: '/stats/tokens',
    name: 'tokenStats',
    icon: 'barChart',
    component: './stats/tokens',
  },
  {
    path: '/global-agent-template',
    name: 'globalAgentTemplate',
    icon: 'robot',
    component: './global-agent-template',
  },
  {
    path: '/capability-management',
    name: 'toolManagement',
    icon: 'tool',
    component: './capability-management/index',
  },
  {
    path: '/skill-management',
    name: 'skillManagement',
    icon: 'code',
    component: './skill-management',
  },
  {
    path: '/keyvault',
    name: 'keyVault',
    icon: 'lock',
    component: './keyvault',
  },
  {
    path: '/session',
    name: 'session',
    icon: 'message',
    component: './session',
  },
  {
    path: '/memory-library',
    name: 'memoryLibrary',
    icon: 'database',
    component: './memory-library',
  },
  {
    path: '/diagnostics',
    name: 'diagnostics',
    icon: 'bug',
    routes: [
      { path: '/diagnostics/overview', redirect: '/diagnostics/overview', component: './diagnostics/DiagnosticsPage', name: 'overview' },
      { path: '/diagnostics/timeline', component: './diagnostics/RuntimeTimelinePage', name: 'timeline' },
      { path: '/diagnostics/subagent-runs', component: './diagnostics/SubAgentRunsPage', name: 'subagent-runs' },
    ],
  },
  {
    path: '/runtime-management',
    name: 'runtimeManagement',
    icon: 'cloudServer',
    component: './runtime-management',
  },
  {
    path: '/tool-approval',
    name: 'toolApproval',
    icon: 'safety',
    routes: [
      {
        path: '/tool-approval/allowlist',
        name: 'allowlist',
        icon: 'checkCircle',
        component: './tool-approval/allowlist',
      },
      {
        path: '/tool-approval/audit',
        name: 'audit',
        icon: 'audit',
        component: './tool-approval/audit',
      },
    ],
  },
  {
    path: '/system-config',
    name: 'systemConfig',
    icon: 'setting',
    routes: [
      {
        path: '/system-config/user-management',
        name: 'userManagement',
        icon: 'user',
        component: './user-management',
      },
      {
        path: '/system-config/team-management',
        name: 'teamManagement',
        icon: 'cluster',
        component: './team-management',
      },
      {
        path: '/system-config/role-management',
        name: 'roleManagement',
        icon: 'safety',
        component: './role-management',
      },
    ],
  },
  {
    path: '/bootstrap',
    name: 'bootstrap',
    component: './bootstrap',
    layout: false,
    hideInMenu: true,
  },
  {
    path: '*',
    layout: false,
    component: './404',
  },
];
