import axios from 'axios'
import type { AxiosInstance, InternalAxiosRequestConfig, AxiosResponse } from 'axios'
import { ElMessage, ElMessageBox } from 'element-plus'
import { useUserStore } from '@/stores/user'
import { getToken } from './auth'

const service: AxiosInstance = axios.create({
  baseURL: import.meta.env.VITE_APP_BASE_API,
  timeout: 15000,
})

// 请求拦截
service.interceptors.request.use(
  (config: InternalAxiosRequestConfig) => {
    const token = getToken()
    if (token) {
      config.headers.Authorization = `Bearer ${token}`
    }
    return config
  },
  (error) => Promise.reject(error),
)

// 响应拦截
service.interceptors.response.use(
  (response: AxiosResponse) => {
    const res = response.data

    // 约定 code === 0 为成功
    if (res.code !== undefined && res.code !== 0) {
      ElMessage.error(res.message || '请求失败')

      // 401: Token 过期
      if (res.code === 401) {
        ElMessageBox.confirm('登录已过期，请重新登录', '确认', {
          confirmButtonText: '重新登录',
          cancelButtonText: '取消',
          type: 'warning',
        }).then(() => {
          const userStore = useUserStore()
          userStore.resetToken()
          location.reload()
        })
      }
      return Promise.reject(new Error(res.message || '请求失败'))
    }

    return res.data !== undefined ? res.data : res
  },
  (error) => {
    ElMessage.error(error.message || '网络错误')
    return Promise.reject(error)
  },
)

export default service
