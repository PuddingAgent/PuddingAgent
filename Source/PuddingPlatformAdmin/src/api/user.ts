import request from '@/utils/request'

export function login(data: { username: string; password: string }) {
  return request({
    url: '/user/login',
    method: 'post',
    data,
  }) as Promise<{ token: string }>
}

export function getInfo() {
  return request({
    url: '/user/info',
    method: 'get',
  }) as Promise<{ name: string; avatar: string; roles: string[] }>
}

export function logout() {
  return request({
    url: '/user/logout',
    method: 'post',
  })
}
