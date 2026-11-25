// ── useSubAgentPool 取数测试（任务 B）────────────────────────────
// hook 的职责是把「加载中 / 失败 / 成功（含空数组）」压成显式状态，
// 并保证 retry 是重新取数而不是复用上次结果。
import { act, renderHook, waitFor } from '@testing-library/react';
import { fetchSubAgentPool, fetchSubAgentPoolCapacity } from './api';
import { useSubAgentPool } from './useSubAgentPool';

jest.mock('./api');

// 同上：被 mock 模块的导入绑定不能用于类型标注。
const mockedFetch = fetchSubAgentPool as unknown as jest.Mock;
// 自动 mock 会让这个函数返回 undefined，而 hook 会直接 .then —— 必须给显式实现，
// 否则 effect 同步抛 TypeError，三条用例会一起变红（红的原因跟它们要测的东西无关）。
const mockedCapacity = fetchSubAgentPoolCapacity as unknown as jest.Mock;

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
    mockedCapacity.mockReset();
    mockedCapacity.mockResolvedValue(15);
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

  it('容量取到后透出 capacity；它不影响列表三态', async () => {
    mockedFetch.mockResolvedValue([entry('c')]);
    mockedCapacity.mockResolvedValue(15);

    const { result } = renderHook(() => useSubAgentPool());

    await waitFor(() => expect(result.current.loading).toBe(false));
    await waitFor(() => expect(result.current.capacity).toBe(15));
    expect(result.current.data).toEqual([entry('c')]);
    expect(result.current.error).toBeNull();
  });

  // 这是本组最有价值的一条：容量是纯展示用的数字，它拿不到时
  // 只能让那一栏消失，不得把已经成功取到的列表降级成错误页。
  it('容量请求失败时：capacity=null，但列表仍然是成功态', async () => {
    mockedFetch.mockResolvedValue([entry('d')]);
    mockedCapacity.mockRejectedValue(new Error('容量端点 500'));

    const { result } = renderHook(() => useSubAgentPool());

    await waitFor(() => expect(result.current.loading).toBe(false));
    await waitFor(() => expect(mockedCapacity).toHaveBeenCalled());

    expect(result.current.capacity).toBeNull();
    // 关键：列表没被牵连。
    expect(result.current.data).toEqual([entry('d')]);
    expect(result.current.error).toBeNull();
  });
});
