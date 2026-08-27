import { act, renderHook, waitFor } from '@testing-library/react';
import { getLlmProviderBalance } from '@/services/platform/api';
import { useProviderBalance } from './useProviderBalance';

jest.mock('@/services/platform/api', () => ({
  getLlmProviderBalance: jest.fn(),
}));

const mockedGetBalance = getLlmProviderBalance as jest.Mock;

function balanceDto(overrides: Record<string, unknown> = {}) {
  return {
    providerId: 'deepseek',
    endpoint: 'https://api.deepseek.com/user/balance',
    isAvailable: true,
    balanceInfos: [
      {
        currency: 'CNY',
        totalBalance: 110,
        grantedBalance: 10,
        toppedUpBalance: 100,
      },
    ],
    error: null,
    queriedAt: '2026-08-24T00:00:00Z',
    ...overrides,
  };
}

describe('useProviderBalance', () => {
  beforeEach(() => {
    jest.clearAllMocks();
  });

  it('fetches on mount and exposes the first balance info', async () => {
    mockedGetBalance.mockResolvedValue(balanceDto());

    const { result } = renderHook(() => useProviderBalance('deepseek', true));

    await waitFor(() => expect(result.current.balance).toBe(110));
    expect(result.current.currency).toBe('CNY');
    expect(result.current.errorText).toBeUndefined();
    expect(result.current.loading).toBe(false);
    expect(mockedGetBalance).toHaveBeenCalledWith('deepseek');
  });

  it('exposes granted/topped-up balances and query time from the first info', async () => {
    mockedGetBalance.mockResolvedValue(
      balanceDto({ queriedAt: '2026-08-24T01:02:03Z' }),
    );

    const { result } = renderHook(() => useProviderBalance('deepseek', true));

    await waitFor(() => expect(result.current.balance).toBe(110));
    expect(result.current.grantedBalance).toBe(10);
    expect(result.current.toppedUpBalance).toBe(100);
    expect(result.current.queriedAt).toBe('2026-08-24T01:02:03Z');
  });

  it('degrades to undefined balance with error when upstream is unavailable', async () => {
    mockedGetBalance.mockResolvedValue(
      balanceDto({ isAvailable: false, balanceInfos: [], error: '认证失败' }),
    );

    const { result } = renderHook(() => useProviderBalance('deepseek', true));

    await waitFor(() => expect(result.current.errorText).toBe('认证失败'));
    expect(result.current.balance).toBeUndefined();
  });

  it('swallows request exceptions into errorText instead of throwing', async () => {
    mockedGetBalance.mockRejectedValue(new Error('网络错误'));

    const { result } = renderHook(() => useProviderBalance('deepseek', true));

    await waitFor(() => expect(result.current.errorText).toBe('网络错误'));
    expect(result.current.balance).toBeUndefined();
  });

  it('does not fetch when providerId is missing or disabled', () => {
    const { result: noId } = renderHook(() =>
      useProviderBalance(undefined, true),
    );
    const { result: disabled } = renderHook(() =>
      useProviderBalance('deepseek', false),
    );

    expect(mockedGetBalance).not.toHaveBeenCalled();
    expect(noId.current.balance).toBeUndefined();
    expect(disabled.current.balance).toBeUndefined();
  });

  it('manual refresh re-invokes the query', async () => {
    mockedGetBalance.mockResolvedValue(balanceDto());
    const { result } = renderHook(() => useProviderBalance('deepseek', true));
    await waitFor(() => expect(result.current.balance).toBe(110));

    mockedGetBalance.mockResolvedValue(
      balanceDto({
        balanceInfos: [
          { currency: 'CNY', totalBalance: 90, grantedBalance: 0, toppedUpBalance: 90 },
        ],
      }),
    );
    // 新增 loading 状态后 refresh 内含同步 setState，需 act 包裹避免告警
    act(() => {
      result.current.refresh();
    });

    await waitFor(() => expect(result.current.balance).toBe(90));
    expect(mockedGetBalance).toHaveBeenCalledTimes(2);
  });

  it('toggles loading during a manual refresh and clears on settle', async () => {
    mockedGetBalance.mockResolvedValue(balanceDto());
    const { result } = renderHook(() => useProviderBalance('deepseek', true));
    await waitFor(() => expect(result.current.balance).toBe(110));
    expect(result.current.loading).toBe(false);

    let rejectNext!: (err: Error) => void;
    mockedGetBalance.mockImplementationOnce(
      () =>
        new Promise((_resolve, reject) => {
          rejectNext = reject;
        }),
    );

    act(() => {
      result.current.refresh();
    });
    // 请求在途：loading 置位（供徽标旋转反馈）
    expect(result.current.loading).toBe(true);

    await act(async () => {
      rejectNext(new Error('超时'));
    });
    await waitFor(() => {
      expect(result.current.loading).toBe(false);
      expect(result.current.errorText).toBe('超时');
    });
  });
});
