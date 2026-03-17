import { createRouter, createWebHistory } from 'vue-router'
import type { RouteRecordRaw } from 'vue-router'
import Layout from '@/layout/index.vue'

/**
 * constantRoutes - 不需要权限判断的路由
 */
export const constantRoutes: RouteRecordRaw[] = [
  {
    path: '/redirect',
    component: Layout,
    meta: { hidden: true },
    children: [
      {
        path: '/redirect/:path(.*)',
        component: () => import('@/views/redirect/index.vue'),
      },
    ],
  },
  {
    path: '/login',
    component: () => import('@/views/login/index.vue'),
    meta: { hidden: true },
  },
  {
    path: '/404',
    component: () => import('@/views/error/404.vue'),
    meta: { hidden: true },
  },
  {
    path: '/',
    component: Layout,
    redirect: '/dashboard',
    children: [
      {
        path: 'dashboard',
        name: 'Dashboard',
        component: () => import('@/views/dashboard/index.vue'),
        meta: { title: '首页', icon: 'HomeFilled', affix: true },
      },
    ],
  },
]

/**
 * asyncRoutes - 需要权限判断的路由（后续扩展）
 */
export const asyncRoutes: RouteRecordRaw[] = [
  // Workspace 管理
  {
    path: '/workspace',
    component: Layout,
    redirect: '/workspace/list',
    meta: { title: '工作空间', icon: 'OfficeBuilding' },
    children: [
      {
        path: 'list',
        name: 'WorkspaceList',
        component: () => import('@/views/workspace/list.vue'),
        meta: { title: '空间列表', icon: 'List' },
      },
    ],
  },
  // Agent 模板
  {
    path: '/agent',
    component: Layout,
    redirect: '/agent/list',
    meta: { title: '智能体', icon: 'Cpu' },
    children: [
      {
        path: 'list',
        name: 'AgentList',
        component: () => import('@/views/agent/list.vue'),
        meta: { title: 'Agent 模板', icon: 'List' },
      },
    ],
  },
  // 审计
  {
    path: '/audit',
    component: Layout,
    redirect: '/audit/index',
    meta: { title: '审计', icon: 'Document' },
    children: [
      {
        path: 'index',
        name: 'Audit',
        component: () => import('@/views/audit/index.vue'),
        meta: { title: '审计事件', icon: 'Document' },
      },
    ],
  },
  // 审批
  {
    path: '/approval',
    component: Layout,
    redirect: '/approval/index',
    meta: { title: '审批', icon: 'Bell' },
    children: [
      {
        path: 'index',
        name: 'Approval',
        component: () => import('@/views/approval/index.vue'),
        meta: { title: '审批管理', icon: 'Bell' },
      },
    ],
  },
  // Runtime 会话
  {
    path: '/session',
    component: Layout,
    redirect: '/session/index',
    meta: { title: 'Runtime 会话', icon: 'Connection' },
    children: [
      {
        path: 'index',
        name: 'Session',
        component: () => import('@/views/session/index.vue'),
        meta: { title: 'Runtime 会话', icon: 'Connection' },
      },
    ],
  },
  // 对话测试
  {
    path: '/chat',
    component: Layout,
    redirect: '/chat/index',
    meta: { title: '对话测试', icon: 'ChatDotRound' },
    children: [
      {
        path: 'index',
        name: 'Chat',
        component: () => import('@/views/chat/index.vue'),
        meta: { title: '对话测试', icon: 'ChatDotRound' },
      },
    ],
  },
  // 404 必须放最后
  { path: '/:pathMatch(.*)*', redirect: '/404', meta: { hidden: true } },
]

const router = createRouter({
  history: createWebHistory(import.meta.env.BASE_URL),
  routes: constantRoutes,
  scrollBehavior: () => ({ top: 0 }),
})

export function resetRouter() {
  const newRouter = createRouter({
    history: createWebHistory(import.meta.env.BASE_URL),
    routes: constantRoutes,
    scrollBehavior: () => ({ top: 0 }),
  })
  // 用新 router 的 matcher 替换旧的
  const routerAny = router as any
  routerAny.matcher = (newRouter as any).matcher
}

export default router
