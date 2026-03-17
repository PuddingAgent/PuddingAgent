import { defineStore } from 'pinia'
import { login, logout, getInfo } from '@/api/user'
import { getToken, setToken, removeToken } from '@/utils/auth'
import router, { asyncRoutes, constantRoutes } from '@/router'
import type { RouteRecordRaw } from 'vue-router'

interface UserState {
  token: string
  name: string
  avatar: string
  roles: string[]
  routes: RouteRecordRaw[]
}

function hasPermission(roles: string[], route: RouteRecordRaw): boolean {
  if (route.meta?.roles) {
    return roles.some((role) => (route.meta!.roles as string[]).includes(role))
  }
  return true
}

function filterAsyncRoutes(routes: RouteRecordRaw[], roles: string[]): RouteRecordRaw[] {
  const res: RouteRecordRaw[] = []
  routes.forEach((route) => {
    const tmp = { ...route }
    if (hasPermission(roles, tmp)) {
      if (tmp.children) {
        tmp.children = filterAsyncRoutes(tmp.children, roles)
      }
      res.push(tmp)
    }
  })
  return res
}

export const useUserStore = defineStore('user', {
  state: (): UserState => ({
    token: getToken() || '',
    name: '',
    avatar: '',
    roles: [],
    routes: [],
  }),

  actions: {
    async login(userInfo: { username: string; password: string }) {
      const { username, password } = userInfo
      const data = await login({ username: username.trim(), password })
      this.token = data.token
      setToken(data.token)
    },

    async getInfo() {
      const data = await getInfo()
      if (!data.roles || data.roles.length === 0) {
        throw new Error('用户必须至少拥有一个角色')
      }
      this.name = data.name
      this.avatar = data.avatar
      this.roles = data.roles
    },

    generateRoutes() {
      let accessedRoutes: RouteRecordRaw[]
      if (this.roles.includes('admin')) {
        accessedRoutes = asyncRoutes
      } else {
        accessedRoutes = filterAsyncRoutes(asyncRoutes, this.roles)
      }
      // 动态添加路由
      accessedRoutes.forEach((route) => {
        router.addRoute(route)
      })
      this.routes = constantRoutes.concat(accessedRoutes)
    },

    async logout() {
      try {
        await logout()
      } finally {
        this.resetToken()
      }
    },

    resetToken() {
      this.token = ''
      this.name = ''
      this.avatar = ''
      this.roles = []
      this.routes = []
      removeToken()
    },
  },
})
