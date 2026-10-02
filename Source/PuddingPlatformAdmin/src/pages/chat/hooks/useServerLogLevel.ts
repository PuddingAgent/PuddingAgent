import { useCallback, useEffect, useState } from 'react';
import { loadServerLogLevel, setServerLogLevel } from '../client/agentChatApi';

/** 会放大日志量的档位（与后端 `PuddingLogLevelStore.IsVerboseEnabled` 同一判定）。 */
export const SERVER_LOG_LEVEL_DEBUG = 'Debug';

/** 关闭调试后的落点：默认级别。 */
export const SERVER_LOG_LEVEL_DEFAULT = 'Information';

export interface ServerLogLevelState {
  /** 当前生效级别（Serilog 正式名）。 */
  level: string;
  /** 是否处于会放大日志量的档位。 */
  isVerbose: boolean;
  /** 是否正在读写。 */
  busy: boolean;
  /** 最近一次失败原因（成功时为 null）。 */
  error: string | null;
  /** 重新从服务端读取。 */
  refresh: () => Promise<void>;
  /** 在 Debug 与默认级别之间切换；返回是否成功（组件据此提示用户）。 */
  toggle: () => Promise<boolean>;
}

const errorText = (error: unknown): string => {
  if (error instanceof Error && error.message) return error.message;
  const status = (error as { response?: { status?: unknown } })?.response?.status;
  return typeof status === 'number'
    ? `服务端返回 ${status}`
    : '无法连接服务端';
};

/**
 * 服务端调试日志开关（前端 Debug 按钮的状态源）。
 *
 * 为什么放在这里而不是各页面自己存：级别是**服务端**的全进程状态，
 * 界面必须显示"当前到底是什么"，否则用户会以为"我点过 Debug 了"却在日志里什么也看不到。
 *
 * 语义约定：
 * · 挂载时读一次当前级别（一个轻量 GET），失败**不阻塞界面**，只记录 error；
 * · `toggle` 成功后会立刻用服务端返回值刷新本地状态（以服务端为准，不乐观更新：
 *   落盘失败时后端不会生效，前端也不该显示成已开启）；
 * · 用 `busy` 防止连点导致两次相反请求竞态。
 */
export const useServerLogLevel = (): ServerLogLevelState => {
  const [level, setLevel] = useState<string>(SERVER_LOG_LEVEL_DEFAULT);
  const [isVerbose, setIsVerbose] = useState<boolean>(false);
  const [busy, setBusy] = useState<boolean>(false);
  const [error, setError] = useState<string | null>(null);

  const apply = useCallback(
    (snapshot: { level: string; isVerbose: boolean }) => {
      setLevel(snapshot.level);
      setIsVerbose(snapshot.isVerbose);
    },
    [],
  );

  const refresh = useCallback(async () => {
    try {
      apply(await loadServerLogLevel());
      setError(null);
    } catch (err) {
      setError(errorText(err));
    }
  }, [apply]);

  useEffect(() => {
    void refresh();
  }, [refresh]);

  const toggle = useCallback(async (): Promise<boolean> => {
    if (busy) return false;
    setBusy(true);
    try {
      const target = isVerbose
        ? SERVER_LOG_LEVEL_DEFAULT
        : SERVER_LOG_LEVEL_DEBUG;
      // 以服务端返回为准（后端"先落盘、后生效"，失败时不会改级别）。
      apply(await setServerLogLevel(target));
      setError(null);
      return true;
    } catch (err) {
      setError(errorText(err));
      return false;
    } finally {
      setBusy(false);
    }
  }, [apply, busy, isVerbose]);

  return { level, isVerbose, busy, error, refresh, toggle };
};
