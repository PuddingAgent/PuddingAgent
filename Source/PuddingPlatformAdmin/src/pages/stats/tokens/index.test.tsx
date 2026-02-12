import { render, screen, waitFor } from '@testing-library/react';
import * as React from 'react';
import TokenStatsPage from './index';
import { getContextLayerTokenStats, getMonthlyTokenStats, getTokenStatsSeries } from '@/services/platform/api';

jest.mock('@/services/platform/api', () => ({
  getContextLayerTokenStats: jest.fn(),
  getMonthlyTokenStats: jest.fn(),
  getTokenStatsSeries: jest.fn(),
  rebuildTokenEvents: jest.fn(),
}));

describe('TokenStatsPage', () => {
  beforeEach(() => {
    (getMonthlyTokenStats as jest.Mock).mockResolvedValue({
      yearMonth: '2026-06',
      totalPromptTokens: 1000,
      totalCompletionTokens: 500,
      totalCacheHitTokens: 300,
      totalCacheMissTokens: 700,
      cacheHitRate: 0.3,
      inputCost: 2,
      cacheHitCost: 0.5,
      outputCost: 2.5,
      totalCost: 5,
      totalRequests: 2,
      byProvider: [
        {
          providerId: 'deepseek',
          promptTokens: 1000,
          completionTokens: 500,
          cacheHitTokens: 300,
          cacheMissTokens: 700,
          cacheHitRate: 0.3,
          inputCost: 2,
          cacheHitCost: 0.5,
          outputCost: 2.5,
          totalCost: 5,
          requestCount: 2,
          models: [
            {
              modelId: 'deepseek-chat',
              promptTokens: 1000,
              completionTokens: 500,
              cacheHitTokens: 300,
              cacheMissTokens: 700,
              cacheHitRate: 0.3,
              inputCost: 2,
              cacheHitCost: 0.5,
              outputCost: 2.5,
              totalCost: 5,
              requestCount: 2,
            },
          ],
        },
      ],
    });
    (getTokenStatsSeries as jest.Mock).mockResolvedValue({
      yearMonth: '2026-06',
      year: 2026,
      monthly: Array.from({ length: 12 }, (_, index) => ({
        period: `2026-${String(index + 1).padStart(2, '0')}`,
        cacheMissTokens: index === 5 ? 700 : 0,
        cacheHitTokens: index === 5 ? 300 : 0,
        completionTokens: index === 5 ? 500 : 0,
        requestCount: index === 5 ? 2 : 0,
        inputCost: index === 5 ? 2 : 0,
        cacheHitCost: index === 5 ? 0.5 : 0,
        outputCost: index === 5 ? 2.5 : 0,
        totalCost: 5,
      })),
      daily: Array.from({ length: 30 }, (_, index) => ({
        period: `2026-06-${String(index + 1).padStart(2, '0')}`,
        cacheMissTokens: index === 1 ? 700 : 0,
        cacheHitTokens: index === 1 ? 300 : 0,
        completionTokens: index === 1 ? 500 : 0,
        requestCount: index === 1 ? 2 : 0,
        inputCost: index === 1 ? 2 : 0,
        cacheHitCost: index === 1 ? 0.5 : 0,
        outputCost: index === 1 ? 2.5 : 0,
        totalCost: 5,
      })),
    });
    (getContextLayerTokenStats as jest.Mock).mockResolvedValue({
      from: '2026-06-01T00:00:00.000Z',
      to: '2026-06-30T23:59:59.999Z',
      totalEvents: 2,
      totalLayerTokens: 200,
      layers: [
        {
          layerName: 'L0-STATIC',
          layerOrder: 0,
          layerRole: 'stable_prefix',
          calls: 2,
          tokenCount: 90,
          tokenShare: 0.45,
          avgTokens: 45,
          medianTokens: 45,
          p95Tokens: 50,
          estimatedHitTokens: 90,
          estimatedMissTokens: 0,
          avgCacheHitRate: 1,
          medianCacheHitRate: 1,
          changeCount: 0,
          changeRate: 0,
          distinctHashes: 1,
          changeReasons: {},
        },
        {
          layerName: 'L5-RECENT',
          layerOrder: 5,
          layerRole: 'dynamic_history',
          calls: 2,
          tokenCount: 110,
          tokenShare: 0.55,
          avgTokens: 55,
          medianTokens: 55,
          p95Tokens: 60,
          estimatedHitTokens: 30,
          estimatedMissTokens: 80,
          avgCacheHitRate: 0.25,
          medianCacheHitRate: 0.25,
          changeCount: 1,
          changeRate: 0.5,
          distinctHashes: 2,
          changeReasons: [{ reason: 'history_changed', count: 1 }],
        },
        {
          layerName: 'L0-INBOUND',
          layerOrder: 3,
          layerRole: 'runtime_context',
          calls: 2,
          tokenCount: 0,
          tokenShare: 0,
          avgTokens: 0,
          medianTokens: 0,
          p95Tokens: 0,
          estimatedHitTokens: 0,
          estimatedMissTokens: 0,
          avgCacheHitRate: undefined,
          medianCacheHitRate: undefined,
          changeCount: 0,
          changeRate: 0,
          distinctHashes: 1,
          changeReasons: {},
        },
      ],
    });
  });

  afterEach(() => {
    jest.clearAllMocks();
  });

  it('uses RMB as the only visible fee unit', async () => {
    render(<TokenStatsPage />);

    await waitFor(() => {
      expect(screen.getByText('费用构成 (RMB)')).toBeTruthy();
    });

    expect(screen.queryByText('费用 (USD)')).not.toBeTruthy();
    await waitFor(() => {
      expect(document.body.textContent).toContain('¥5.0000');
    });
    expect(document.body.textContent).toContain('¥0.5000');
    expect(document.body.textContent).toContain('¥2.0000');
    expect(document.body.textContent).toContain('¥2.5000');
    expect(document.body.textContent).not.toContain('$5.0000');
    expect(document.body.textContent).not.toContain('（USD）');
  });

  it('renders monthly and daily token breakdown charts', async () => {
    render(<TokenStatsPage />);

    await waitFor(() => {
      expect(getTokenStatsSeries).toHaveBeenCalledWith('2026-06', undefined, undefined);
    });

    expect(screen.getByText('按月趋势')).toBeTruthy();
    expect(screen.getByText('按日趋势')).toBeTruthy();
    expect(screen.getAllByText('输入（未命中缓存）').length).toBeGreaterThan(0);
    expect(screen.getAllByText('输入（命中缓存）').length).toBeGreaterThan(0);
    expect(screen.getAllByText('输出').length).toBeGreaterThan(0);
    expect(document.querySelectorAll('[data-testid="token-series-chart"]')).toHaveLength(2);
  });

  it('renders context layer cache and volatility stats', async () => {
    render(<TokenStatsPage />);

    await waitFor(() => {
      expect(getContextLayerTokenStats).toHaveBeenCalled();
    });

    await waitFor(() => {
      expect(screen.getByText('L0-STATIC')).toBeTruthy();
    });
    expect(screen.getByText('上下文分层缓存分析')).toBeTruthy();
    expect(screen.getByText('L5-RECENT')).toBeTruthy();
    expect(screen.getAllByText('Token 压力').length).toBeGreaterThan(0);
    expect(screen.getAllByText('缓存表现').length).toBeGreaterThan(0);
    expect(screen.getByText('L0-INBOUND')).toBeTruthy();
    expect(screen.getByText('无缓存样本')).toBeTruthy();
    expect(screen.getByText('未参与')).toBeTruthy();
    expect(document.body.textContent).toContain('history_changed: 1');
  });

  it('renders stable provider and model filter defaults', async () => {
    render(<TokenStatsPage />);

    await waitFor(() => {
      expect(getMonthlyTokenStats).toHaveBeenCalledWith('2026-06', undefined, undefined);
    });

    expect(screen.getByText('全部 Provider')).toBeTruthy();
    expect(screen.getByText('全部模型')).toBeTruthy();
  });
});
