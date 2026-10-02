import { act, renderHook, waitFor } from '@testing-library/react';
import * as api from '../client/agentChatApi';
import { useServerLogLevel } from './useServerLogLevel';

jest.mock('../client/agentChatApi');

const snapshot = (level: string, isVerbose: boolean) => ({
  level,
  isVerbose,
  supportedLevels: ['Verbose', 'Debug', 'Information', 'Warning', 'Error', 'Fatal'],
  configFile: 'D:/data/config/logging.json',
});

const loadMock = api.loadServerLogLevel as jest.Mock;
const setMock = api.setServerLogLevel as jest.Mock;

describe('useServerLogLevel（前端 Debug 按钮的状态源）', () => {
  beforeEach(() => {
    jest.clearAllMocks();
  });

  it('挂载时读取服务端当前级别', async () => {
    loadMock.mockResolvedValue(snapshot('Information', false));

    const { result } = renderHook(() => useServerLogLevel());

    await waitFor(() => expect(result.current.level).toBe('Information'));
    expect(result.current.isVerbose).toBe(false);
    expect(result.current.error).toBeNull();
  });

  it('标准级别下 toggle 切到 Debug，并以服务端返回为准', async () => {
    loadMock.mockResolvedValue(snapshot('Information', false));
    setMock.mockResolvedValue(snapshot('Debug', true));

    const { result } = renderHook(() => useServerLogLevel());
    await waitFor(() => expect(result.current.level).toBe('Information'));

    await act(async () => {
      await result.current.toggle();
    });

    expect(setMock).toHaveBeenCalledWith('Debug');
    expect(result.current.isVerbose).toBe(true);
    expect(result.current.level).toBe('Debug');
  });

  it('Debug 档下 toggle 切回标准级别（一键回退，避免忘记关）', async () => {
    loadMock.mockResolvedValue(snapshot('Debug', true));
    setMock.mockResolvedValue(snapshot('Information', false));

    const { result } = renderHook(() => useServerLogLevel());
    await waitFor(() => expect(result.current.isVerbose).toBe(true));

    await act(async () => {
      await result.current.toggle();
    });

    expect(setMock).toHaveBeenCalledWith('Information');
    expect(result.current.isVerbose).toBe(false);
  });

  it('读取失败只记录错误，不阻塞界面（聊天页未 mock request 的真实场景）', async () => {
    loadMock.mockRejectedValue(new TypeError('request is not a function'));

    const { result } = renderHook(() => useServerLogLevel());

    await waitFor(() => expect(result.current.error).toBeTruthy());
    expect(result.current.isVerbose).toBe(false);
    expect(result.current.level).toBe('Information');
  });

  it('切换失败时不做乐观更新：界面必须反映服务端事实', async () => {
    loadMock.mockResolvedValue(snapshot('Information', false));
    setMock.mockRejectedValue(new Error('服务端返回 400'));

    const { result } = renderHook(() => useServerLogLevel());
    await waitFor(() => expect(result.current.level).toBe('Information'));

    let ok = true;
    await act(async () => {
      ok = await result.current.toggle();
    });

    expect(ok).toBe(false);
    expect(result.current.isVerbose).toBe(false);
    expect(result.current.level).toBe('Information');
    expect(result.current.error).toContain('400');
  });
});
