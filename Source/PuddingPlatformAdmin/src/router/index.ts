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
  // 示例：workspace 管理
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
        meta: { title: '智能体列表', icon: 'List' },
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
