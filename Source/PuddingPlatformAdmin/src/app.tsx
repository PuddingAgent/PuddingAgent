import { BulbOutlined, LinkOutlined, MoonOutlined, SunOutlined } from '@ant-design/icons';
import type { Settings as LayoutSettings } from '@ant-design/pro-components';
import { SettingDrawer } from '@ant-design/pro-components';
import type { RequestConfig, RunTimeLayoutConfig } from '@umijs/max';
import { history, Link } from '@umijs/max';
import { Button, ConfigProvider, Tooltip, theme as antdTheme } from 'antd';
import React, { createContext, useCallback, useContext, useEffect, useMemo, useState } from 'react';
import {
  AvatarDropdown,
  AvatarName,
  Footer,
  Question,
  SelectLang,
} from '@/components';
import { currentUser as queryCurrentUser } from '@/services/ant-design-pro/api';
import defaultSettings, { DARK_NAV_THEME } from '../config/defaultSettings';
import { errorConfig } from './requestErrorConfig';
import { useGlobalShortcuts } from './hooks/useGlobalShortcuts';
import { registerDebugApi } from '@/utils/debug';
import '@ant-design/v5-patch-for-react-19';
import './global.style';

const isDev = process.env.NODE_ENV === 'development';
const loginPath = '/user/login';
const bootstrapPath = '/bootstrap';
const THEME_MODE_STORAGE_KEY = 'pudding_admin_theme_mode';
const THEME_MEDIA_QUERY = '(prefers-color-scheme: dark)';

type ThemeMode = 'system' | 'light' | 'dark';

interface ThemeModeContextValue {
  themeMode: ThemeMode;
  isDark: boolean;
  setThemeMode: (mode: ThemeMode) => void;
  toggleTheme: () => void;
}

const ThemeModeContext = createContext<ThemeModeContextValue>({
  themeMode: 'system',
  isDark: false,
  setThemeMode: () => {},
  toggleTheme: () => {},
});

const getStoredThemeMode = (): ThemeMode => {
  if (typeof window === 'undefined') {
    return 'system';
  }

  const stored = localStorage.getItem(THEME_MODE_STORAGE_KEY);
  return stored === 'light' || stored === 'dark' || stored === 'system' ? stored : 'system';
};

const getSystemPrefersDark = (): boolean => {
  if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') {
    return false;
  }
  return window.matchMedia(THEME_MEDIA_QUERY).matches;
};

const useThemeMode = () => useContext(ThemeModeContext);

const ThemeProviderContainer: React.FC<{ children: React.ReactNode }> = ({ children }) => {
  useGlobalShortcuts();

  const [themeMode, setThemeModeState] = useState<ThemeMode>(getStoredThemeMode);
  const [systemPrefersDark, setSystemPrefersDark] = useState<boolean>(getSystemPrefersDark);

  useEffect(() => {
    if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') {
      return;
    }

    const media = window.matchMedia(THEME_MEDIA_QUERY);
    const onThemeChange = (event: MediaQueryListEvent) => {
      setSystemPrefersDark(event.matches);
    };

    setSystemPrefersDark(media.matches);
    media.addEventListener('change', onThemeChange);

    return () => {
      media.removeEventListener('change', onThemeChange);
    };
  }, []);

  const isDark = themeMode === 'system' ? systemPrefersDark : themeMode === 'dark';

  const setThemeMode = useCallback((mode: ThemeMode) => {
    setThemeModeState(mode);
    if (typeof window !== 'undefined') {
      localStorage.setItem(THEME_MODE_STORAGE_KEY, mode);
    }
  }, []);

  const toggleTheme = useCallback(() => {
    setThemeMode(isDark ? 'light' : 'dark');
  }, [isDark, setThemeMode]);

  useEffect(() => {
    if (typeof document !== 'undefined') {
      document.documentElement.setAttribute('data-pudding-theme', isDark ? 'dark' : 'light');
    }
  }, [isDark]);

  // 注册 debug API（仅在 ?debug=1 时启用）
  useEffect(() => {
    registerDebugApi({
      getSessionState: (_sessionId) => {
        return null;
      },
      getLastTraceId: () => sessionStorage.getItem('pudding_last_trace_id'),
      getLastSessionId: () => sessionStorage.getItem('pudding_last_session_id'),
      getLastMessageId: () => sessionStorage.getItem('pudding_last_message_id'),
      exportTimeline: () => null,
      clearDebugEvents: () => {
        sessionStorage.removeItem('pudding_last_trace_id');
        sessionStorage.removeItem('pudding_last_session_id');
        sessionStorage.removeItem('pudding_last_message_id');
      },
    });
  }, []);

  const contextValue = useMemo<ThemeModeContextValue>(
    () => ({
      themeMode,
      isDark,
      setThemeMode,
      toggleTheme,
    }),
    [isDark, setThemeMode, themeMode, toggleTheme],
  );

  const themeConfig = useMemo(
    () => ({
      cssVar: true,
      algorithm: isDark ? antdTheme.darkAlgorithm : antdTheme.defaultAlgorithm,
      token: {
        colorPrimary: '#7c3aed',
        colorBgLayout: isDark ? '#0B1020' : '#F5F0E8',
        borderRadius: 8,
        borderRadiusLG: 8,
        borderRadiusXL: 16,
      },
    }),
    [isDark],
  );

  return (
    <ThemeModeContext.Provider value={contextValue}>
      <ConfigProvider theme={themeConfig}>{children}</ConfigProvider>
    </ThemeModeContext.Provider>
  );
};

const ThemeToggleAction: React.FC<{
  setInitialState: ((state: any) => void) | undefined;
}> = ({ setInitialState }) => {
  const { isDark, themeMode, toggleTheme, setThemeMode } = useThemeMode();

  useEffect(() => {
    if (!setInitialState) {
      return;
    }

    const nextNavTheme = isDark ? DARK_NAV_THEME : 'light';
    setInitialState((prevState: any) => {
      if (prevState?.settings?.navTheme === nextNavTheme) {
        return prevState;
      }

      return {
        ...prevState,
        settings: {
          ...(prevState?.settings ?? {}),
          navTheme: nextNavTheme,
        },
      };
    });
  }, [isDark, setInitialState]);

  const icon = themeMode === 'system' ? <BulbOutlined /> : isDark ? <MoonOutlined /> : <SunOutlined />;

  const tooltipText =
    themeMode === 'system'
      ? `跟随系统（当前${isDark ? '暗色' : '亮色'}），点击切换到手动模式`
      : isDark
        ? '切换到亮色主题'
        : '切换到暗色主题';

  return (
    <Tooltip title={tooltipText}>
      <Button
        type="text"
        aria-label="切换主题"
        icon={icon}
        onClick={toggleTheme}
        onDoubleClick={() => setThemeMode('system')}
      />
    </Tooltip>
  );
};

const getInitialSettings = (): Partial<LayoutSettings> => {
  const mode = getStoredThemeMode();
  const shouldUseDark = mode === 'dark' || (mode === 'system' && getSystemPrefersDark());
  return {
    ...(defaultSettings as Partial<LayoutSettings>),
    navTheme: shouldUseDark ? DARK_NAV_THEME : 'light',
  };
};

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
      <ThemeToggleAction key="theme-toggle" setInitialState={setInitialState} />,
      <Question key="doc" />,
      <SelectLang key="SelectLang" />,
    ],
    avatarProps: {
      src: initialState?.currentUser?.avatar,
      title: <AvatarName />,
      render: (_, avatarChildren) => {
        return <AvatarDropdown>{avatarChildren}</AvatarDropdown>;
      },
    },
    waterMarkProps: {
      content: initialState?.currentUser?.name,
    },
    footerRender: () => <Footer />,
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
    links: isDev
      ? [
          <Link key="openapi" to="/umi/plugin/openapi" target="_blank">
            <LinkOutlined />
            <span>OpenAPI 文档</span>
          </Link>,
        ]
      : [],
    menuHeaderRender: undefined,
    // 自定义 403 页面
    // unAccessible: <div>unAccessible</div>,
    // 增加一个 loading 的状态
    childrenRender: (children) => {
      // if (initialState?.loading) return <PageLoading />;
      return (
        <>
          {children}
          {isDev && (
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
