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
import { registerDebugApi } from '../../utils/perfEventRuntime';
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

/**
 * Web 主题开关的**作用范围**提示（设计规格 §13.x）。
 *
 * 宿主（Shell）的工具区、窗口装饰与底部状态条由 Desktop 的外观设置决定，Web 这个开关
 * 只改 Web 工作台。规格明确要求：**保留 Web 独立选择就必须标明范围**，不能让两个
 * "看起来都是全局"的开关同时存在却范围不明。实测依据：浅色下 Web 为 #f7f8fa/#ffffff，
 * 而右侧工具区在深浅两张截图里都是 #27272b/#121212（不随此开关变化）。
 */
export const THEME_SCOPE_HINT = '仅 Web 工作台，右侧工具区由 Desktop 外观设置决定';

export interface ThemeToggleCopy {
  tooltipText: string;
  /** 可访问名称：只说"Web 工作台主题"，不暗示它会改整个应用 */
  ariaLabel: string;
}

/**
 * 主题开关的文案（纯函数，便于无 UI 单测"范围提示不被删掉"）。
 */
export const describeThemeToggle = (
  themeMode: ThemeMode,
  isDark: boolean,
): ThemeToggleCopy => {
  const state =
    themeMode === 'system'
      ? `跟随系统（当前${isDark ? '暗色' : '亮色'}）`
      : `${isDark ? '暗色' : '亮色'}`;
  const action =
    themeMode === 'system'
      ? '点击切换到手动模式'
      : `点击切换到${isDark ? '亮色' : '暗色'}`;

  return {
    tooltipText: `外观（${THEME_SCOPE_HINT}）：${state}，${action}`,
    ariaLabel: '切换 Web 工作台主题',
  };
};

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
        // IMG01（设计规格 §3）：单一蓝色强调 + 中性层级；与 global.style.ts 的
        // --pudding-chat-*/--pudding-admin-* 同源，避免 antd 派生 token 继续带出暖米/紫。
        colorPrimary: isDark ? '#91b3ff' : '#2458d3',
        colorBgLayout: isDark ? '#11151b' : '#f7f8fa',
        colorBgContainer: isDark ? '#1a2029' : '#ffffff',
        colorBorder: isDark ? '#445166' : '#d8dee8',
        colorText: isDark ? '#e8edf4' : '#182230',
        colorTextSecondary: isDark ? '#a8b5c7' : '#526174',
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

  // IMG02/§13.x：范围必须在文案里说明（Web 独立开关 + 宿主外观设置并存）
  const { tooltipText, ariaLabel } = describeThemeToggle(themeMode, isDark);

  return (
    <Tooltip title={tooltipText}>
      <Button
        type="text"
        aria-label={ariaLabel}
        icon={icon}
        size={compact ? 'small' : undefined}
        onClick={toggleTheme}
        onDoubleClick={() => setThemeMode('system')}
      />
    </Tooltip>
  );
};
