import type { Settings as LayoutSettings } from '@ant-design/pro-components';
import { SettingDrawer } from '@ant-design/pro-components';
import type { RequestConfig, RunTimeLayoutConfig } from '@umijs/max';
import { history } from '@umijs/max';
import React from 'react';
import {
  AvatarDropdown,
  AvatarName,
} from '@/components';
import { PuddingGlobalActions } from '@/components/GlobalActions';
import { ThemeProviderContainer, getInitialSettings } from '@/components/ThemeMode';
import { currentUser as queryCurrentUser } from '@/services/ant-design-pro/api';
import { errorConfig } from './requestErrorConfig';
import '@ant-design/v5-patch-for-react-19';
import './global.style';

const isDev = process.env.NODE_ENV === 'development';
const showDevTools = isDev && typeof window !== 'undefined' && new URLSearchParams(window.location.search).has('debug');
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

// ProLayout 支持的api https://procomponents.ant.design/components/layout
export const layout: RunTimeLayoutConfig = ({
  initialState,
  setInitialState,
}) => {
  return {
    actionsRender: () => [
      <PuddingGlobalActions
        key="global-actions"
        variant="pro-layout"
        setInitialState={setInitialState}
      />,
    ],
    avatarProps: {
      src: initialState?.currentUser?.avatar,
      title: <AvatarName />,
      render: (_, avatarChildren) => {
        return <AvatarDropdown dropdownTrigger={['click']}>{avatarChildren}</AvatarDropdown>;
      },
    },
    footerRender: false,
    onPageChange: () => {
      const { location } = history;
      // 如果没有登录且不在登录页或初始化页，重定向到登录页
      if (
        !initialState?.currentUser &&
        location.pathname !== loginPath &&
        location.pathname !== bootstrapPath
      ) {
        history.push(loginPath);
      }
    },
    bgLayoutImgList: [],
    links: [],
    menuHeaderRender: undefined,
    // 自定义 403 页面
    // unAccessible: <div>unAccessible</div>,
    // 增加一个 loading 的状态
    childrenRender: (children) => {
      // if (initialState?.loading) return <PageLoading />;
      return (
        <>
          {children}
          {showDevTools && (
            <SettingDrawer
              disableUrlParams
              enableDarkTheme
              settings={initialState?.settings}
              onSettingChange={(settings) => {
                setInitialState((preInitialState) => ({
                  ...preInitialState,
                  settings,
                }));
              }}
            />
          )}
        </>
      );
    },
    ...initialState?.settings,
  };
};

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
