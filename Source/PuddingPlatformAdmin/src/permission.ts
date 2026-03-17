import router from './router'
import NProgress from 'nprogress'
import 'nprogress/nprogress.css'
import { useUserStore } from './stores/user'
import { getToken } from './utils/auth'
import settings from './settings'

NProgress.configure({ showSpinner: false })

const whiteList = ['/login']

router.beforeEach(async (to, _from, next) => {
  NProgress.start()
  document.title = `${to.meta.title || ''} - ${settings.title}`

  const hasToken = getToken()

  if (hasToken) {
    if (to.path === '/login') {
      next({ path: '/' })
      NProgress.done()
    } else {
      const userStore = useUserStore()
      const hasRoles = userStore.roles && userStore.roles.length > 0

      if (hasRoles) {
        next()
      } else {
        try {
          await userStore.getInfo()
          userStore.generateRoutes()
          // 确保动态路由已加载完毕，使用 replace 跳转
          next({ ...to, replace: true })
        } catch {
          await userStore.resetToken()
          next(`/login?redirect=${to.path}`)
          NProgress.done()
        }
      }
    }
  } else {
    if (whiteList.includes(to.path)) {
      next()
    } else {
      next(`/login?redirect=${to.path}`)
      NProgress.done()
    }
  }
})

router.afterEach(() => {
  NProgress.done()
})
