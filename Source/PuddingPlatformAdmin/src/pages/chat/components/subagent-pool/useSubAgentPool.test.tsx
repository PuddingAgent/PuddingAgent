// ── useSubAgentPool 取数测试（任务 B）────────────────────────────
// hook 的职责是把「加载中 / 失败 / 成功（含空数组）」压成显式状态，
// 并保证 retry 是重新取数而不是复用上次结果。
import { act, renderHook, waitFor } from '@testing-library/react';
import { fetchSubAgentPool } from './api';
import { useSubAgentPool } from './useSubAgentPool';

jest.mock('./api');

// 同上：被 mock 模块的导入绑定不能用于类型标注。
const mockedFetch = fetchSubAgentPool as unknown as jest.Mock;

const entry = (poolId: string) => ({
  poolId,
  name: `子代理 ${poolId}`,
  role: null,
  responsibility: null,
  avatarKey: null,
  modelId: null,
  templateId: null,
  status: 'idle',
  taskCount: 0,
  lastUsedAt: null,
  lastSuccessAt: null,
  subSessionId: `sub-${poolId}`,
  createdAt: '2026-11-01T00:00:00Z',
});

describe('useSubAgentPool', () => {
  beforeEach(() => {
    mockedFetch.mockReset();
  });

  it('请求中 loading=true 且 data 为 null', async () => {
    let resolveFetch: (value: unknown[]) => void = () => {};
    mockedFetch.mockImplementation(
      () =>
        new Promise<unknown[]>((resolve) => {
          resolveFetch = resolve;
        }),
    );

    const { result } = renderHook(() => useSubAgentPool());

    expect(result.current.loading).toBe(true);
    expect(result.current.data).toBeNull();
    expect(result.current.error).toBeNull();

    await act(async () => {
      resolveFetch([entry('a')]);
    });

    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(result.current.data).toEqual([entry('a')]);
    expect(result.current.error).toBeNull();
  });

  it('空数组是成功态：loading=false、error=null、data=[]', async () => {
    mockedFetch.mockResolvedValue([]);

    const { result } = renderHook(() => useSubAgentPool());

    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(result.current.data).toEqual([]);
    expect(result.current.error).toBeNull();
  });

  it('失败时 data=null 且 error 有文案，reload 会重新取数', async () => {
    mockedFetch.mockRejectedValueOnce(new Error('网络不可用'));
    mockedFetch.mockResolvedValueOnce([entry('b')]);

    const { result } = renderHook(() => useSubAgentPool());

    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(result.current.data).toBeNull();
    expect(result.current.error).toBe('网络不可用');

    act(() => result.current.reload());

    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(result.current.error).toBeNull();
    expect(result.current.data).toEqual([entry('b')]);
    expect(mockedFetch).toHaveBeenCalledTimes(2);
  });
});
