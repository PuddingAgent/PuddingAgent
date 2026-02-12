/**
 * ThemeMode — 统一主题管理模块
 *
 * 从 app.tsx 提升为可复用模块，同时被 ProLayout（Console）和 Chat 消费。
 * localStorage key: pudding_admin_theme_mode
 */

import { BulbOutlined, MoonOutlined, SunOutlined } from '@ant-design/icons';
import { Button, ConfigProvider, Tooltip, theme as antdTheme } from 'antd';
import React, { createContext, useCallback, useContext, useEffect, useMemo, useState } from 'react';
import { DARK_NAV_THEME } from '../../../config/defaultSettings';
import type { Settings as LayoutSettings } from '@ant-design/pro-components';
import { useGlobalShortcuts } from '../../hooks/useGlobalShortcuts';
import { registerDebugApi } from '../../utils/debug';
import { getPuddingPopupContainer } from '../../utils/popupContainer';

export const THEME_MODE_STORAGE_KEY = 'pudding_admin_theme_mode';
const THEME_MEDIA_QUERY = '(prefers-color-scheme: dark)';

export type ThemeMode = 'system' | 'light' | 'dark';

export interface ThemeModeContextValue {
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

export const useThemeMode = () => useContext(ThemeModeContext);

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

export const getInitialSettings = (): Partial<LayoutSettings> => {
  const mode = getStoredThemeMode();
  const shouldUseDark = mode === 'dark' || (mode === 'system' && getSystemPrefersDark());
  return {
    navTheme: shouldUseDark ? DARK_NAV_THEME : 'light',
  } as Partial<LayoutSettings>;
};

/**
 * 主题 Provider 容器：包裹整个应用，提供 ThemeModeContext 和 AntD ConfigProvider。
 */
export const ThemeProviderContainer: React.FC<{ children: React.ReactNode }> = ({ children }) => {
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
    if (typeof media.addEventListener === 'function') {
      media.addEventListener('change', onThemeChange);
    } else {
      media.addListener?.(onThemeChange);
    }
    return () => {
      if (typeof media.removeEventListener === 'function') {
        media.removeEventListener('change', onThemeChange);
      } else {
        media.removeListener?.(onThemeChange);
      }
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
      getSessionState: (_sessionId) => null,
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
    () => ({ themeMode, isDark, setThemeMode, toggleTheme }),
    [isDark, setThemeMode, themeMode, toggleTheme],
  );

  const themeConfig = useMemo(
    () => ({
      cssVar: true,
      algorithm: isDark ? antdTheme.darkAlgorithm : antdTheme.defaultAlgorithm,
      token: {
        colorPrimary: isDark ? '#a78bfa' : '#7c3aed',
        colorBgLayout: isDark ? '#0b1020' : '#f5f0e8',
        colorBgContainer: isDark ? '#172033' : '#fafaf7',
        colorBorder: isDark ? 'rgba(167, 139, 250, 0.18)' : 'rgba(92, 74, 58, 0.12)',
        colorText: isDark ? '#f8fafc' : '#1a1a2e',
        colorTextSecondary: isDark ? '#cbd5e1' : '#5c4a3a',
        borderRadius: 8,
        borderRadiusLG: 8,
        borderRadiusXL: 12,
        controlHeight: 36,
        controlHeightSM: 30,
      },
    }),
    [isDark],
  );

  return (
    <ThemeModeContext.Provider value={contextValue}>
      <ConfigProvider theme={themeConfig} getPopupContainer={getPuddingPopupContainer}>
        {children}
      </ConfigProvider>
    </ThemeModeContext.Provider>
  );
};

interface ThemeToggleActionProps {
  setInitialState?: (state: any) => void;
  /** Chat 模式下使用紧凑样式 */
  compact?: boolean;
}

/**
 * 主题切换按钮 — 共享组件，Console 和 Chat 均可使用。
 */
export const ThemeToggleAction: React.FC<ThemeToggleActionProps> = ({
  setInitialState,
  compact,
}) => {
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

  const icon =
    themeMode === 'system' ? (
      <BulbOutlined />
    ) : isDark ? (
      <MoonOutlined />
    ) : (
      <SunOutlined />
    );

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
        size={compact ? 'small' : undefined}
        onClick={toggleTheme}
        onDoubleClick={() => setThemeMode('system')}
      />
    </Tooltip>
  );
};
