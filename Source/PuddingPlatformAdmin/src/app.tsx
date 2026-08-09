import '@ant-design/v5-patch-for-react-19';
import type { Settings as LayoutSettings } from '@ant-design/pro-components';
import type { RequestConfig } from '@umijs/max';
import { history } from '@umijs/max';
import React from 'react';
import { getInitialSettings, ThemeProviderContainer } from '@/components/ThemeMode';
import { currentUser as queryCurrentUser } from '@/services/ant-design-pro/api';
import { errorConfig } from './requestErrorConfig';
import './global.style';

const loginPath = '/user/login';
const bootstrapPath = '/bootstrap';

export const rootContainer = (container: React.ReactNode) => {
  return <ThemeProviderContainer>{container}</ThemeProviderContainer>;
};

/**
 * @see https://umijs.org/docs/api/runtime-config#getinitialstate
 * */
export async function getInitialState(): Promise<{
  settings?: Partial<LayoutSettings>;
  currentUser?: API.CurrentUser;
  loading?: boolean;
  fetchUserInfo?: () => Promise<API.CurrentUser | undefined>;
}> {
  const fetchUserInfo = async () => {
    try {
      const msg = await queryCurrentUser({
        skipErrorHandler: true,
      });
      return msg.data;
    } catch (_error) {
      history.push(loginPath);
    }
    return undefined;
  };

  const checkBootstrapAndRedirect = async () => {
    try {
      const res = await fetch('/api/bootstrap/status');
      if (res.status === 403) {
        history.push(loginPath);
        return;
      }
      const data = await res.json();
      if (data.needsSetup) {
        history.push(bootstrapPath);
      } else {
        history.push(loginPath);
      }
    } catch {
      history.push(loginPath);
    }
  };

  const { location } = history;

  // Bootstrap / Login pages: skip auth check entirely
  if ([loginPath, bootstrapPath].includes(location.pathname)) {
    return {
      fetchUserInfo,
      settings: getInitialSettings(),
    };
  }

  const token = localStorage.getItem('pudding_token');

  if (!token) {
    // No token → check bootstrap status to decide where to redirect
    await checkBootstrapAndRedirect();
    return {
      fetchUserInfo,
      settings: getInitialSettings(),
    };
  }

  // Has token → try to validate it
  try {
    const msg = await queryCurrentUser({ skipErrorHandler: true });
    return {
      fetchUserInfo,
      currentUser: msg.data,
      settings: getInitialSettings(),
    };
  } catch {
    // Token expired/invalid → clear it and re-check bootstrap status
    localStorage.removeItem('pudding_token');
    await checkBootstrapAndRedirect();
    return {
      fetchUserInfo,
      settings: getInitialSettings(),
    };
  }
}

/**
 * @name request 配置，可以配置错误处理
 * 它基于 axios 和 ahooks 的 useRequest 提供了一套统一的网络请求和错误处理方案。
 * @doc https://umijs.org/docs/max/request#配置
 */
export const request: RequestConfig = {
  // dev 环境通过 UmiJS proxy 转发到 PuddingPlatform 后端；mock 模式下由 mock/ 拦截
  baseURL: '/',
  ...errorConfig,
};
