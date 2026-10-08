import { render, screen } from '@testing-library/react';
import * as React from 'react';
import ComposerStatusDetails from './ComposerStatusDetails';

jest.mock('../styles', () => ({
  useChatStyles: () => ({
    styles: {
      composerStatusDetails: 'composerStatusDetails',
      composerStatusDetailsHeader: 'composerStatusDetailsHeader',
      composerStatusDetailsDot: 'composerStatusDetailsDot',
      composerStatusDetailsTitle: 'composerStatusDetailsTitle',
      composerStatusDetailsGroup: 'composerStatusDetailsGroup',
      composerStatusDetailsGroupTitle: 'composerStatusDetailsGroupTitle',
      composerStatusDetailRow: 'composerStatusDetailRow',
      composerStatusDetailLabel: 'composerStatusDetailLabel',
      composerStatusDetailValue: 'composerStatusDetailValue',
    },
  }),
}));

describe('ComposerStatusDetails', () => {
  const baseSummary = {
    status: 'completed' as const,
    statusLabel: '已完成',
    contextService: 'available' as const,
    index: 'disabled' as const,
    backgroundMemory: 'idle' as const,
    subAgentsRunning: 0,
    modelService: 'available' as const,
  };

  it('formats cache hit rate as an existing 0-100 percentage', () => {
    render(
      <ComposerStatusDetails
        summary={{
          ...baseSummary,
          status: 'thinking' as const,
          statusLabel: '正在整理上下文...',
          token: { used: 13_100, limit: 1_048_600, percentage: 1.25 },
          cacheHitRate: 76,
        }}
      />,
    );

    expect(screen.getByText('76%')).toBeTruthy();
    expect(screen.queryByText('7600%')).toBeNull();
  });

  it('provides the initializing label when no custom status label is set', () => {
    render(
      <ComposerStatusDetails
        summary={{ ...baseSummary, status: 'initializing', statusLabel: '' }}
      />,
    );
    expect(screen.getByText('正在初始化…')).toBeTruthy();
  });

  it('describes token capacity as remaining effective context', () => {
    render(
      <ComposerStatusDetails
        summary={{
          ...baseSummary,
          token: { used: 14_000, limit: 1_048_600, percentage: 1.33 },
        }}
      />,
    );

    expect(screen.getByText('有效上下文')).toBeTruthy();
    expect(screen.getByText('剩余 1034.6k / 1048.6k')).toBeTruthy();
    expect(screen.queryByText('Token')).toBeNull();
  });

  it('uses refreshed remaining tokens when context health supplies it', () => {
    render(
      <ComposerStatusDetails
        summary={{
          ...baseSummary,
          token: {
            used: 14_000,
            limit: 1_048_600,
            percentage: 1.33,
            remaining: 1_010_000,
          },
          contextUsageStatus: 'ready',
        }}
      />,
    );

    expect(screen.getByText('剩余 1010.0k / 1048.6k')).toBeTruthy();
  });

  it('shows context refresh progress before usage data is available', () => {
    render(
      <ComposerStatusDetails
        summary={{ ...baseSummary, contextUsageStatus: 'loading' }}
      />,
    );

    expect(screen.getByText('有效上下文')).toBeTruthy();
    expect(screen.getByText('正在刷新…')).toBeTruthy();
  });

  it('shows cache hit rate as pending before cache metrics are available', () => {
    render(<ComposerStatusDetails summary={baseSummary} />);

    expect(screen.getByText('缓存命中')).toBeTruthy();
    expect(screen.getByText('待计算')).toBeTruthy();
  });

  it('does not render a negative cache hit rate from stale sentinel values', () => {
    render(
      <ComposerStatusDetails summary={{ ...baseSummary, cacheHitRate: -1 }} />,
    );

    expect(screen.getByText('待计算')).toBeTruthy();
    expect(screen.queryByText('-1%')).toBeNull();
  });

  it('labels the windowed cache hit rate with its sample count', () => {
    render(
      <ComposerStatusDetails
        summary={{
          ...baseSummary,
          cacheHitRate: 76,
          cacheHitRateScope: 'windowed',
          cacheHitRateSampleCount: 42,
        }}
      />,
    );

    expect(screen.getByText('76%')).toBeTruthy();
    expect(screen.getByText('本请求窗口(最近42条)')).toBeTruthy();
  });

  it('labels the session-wide cache hit rate so the window value cannot silently win', () => {
    render(
      <ComposerStatusDetails
        summary={{ ...baseSummary, cacheHitRate: 76, cacheHitRateScope: 'session' }}
      />,
    );

    expect(screen.getByText('76%')).toBeTruthy();
    expect(screen.getByText('全会话')).toBeTruthy();
    expect(screen.queryByText(/本请求窗口/)).toBeNull();
  });

  it('renders 未知 for every service status that was never collected', () => {
    render(
      <ComposerStatusDetails
        summary={{
          ...baseSummary,
          contextService: 'unknown',
          index: 'unknown',
          backgroundMemory: 'unknown',
          modelService: 'unknown',
        }}
      />,
    );

    // 「未知」与「未启用」必须不同文案：不可知不得被读成关。
    expect(screen.getAllByText('未知')).toHaveLength(4);
    expect(screen.queryByText('可用')).toBeNull();
  });

  it('renders capacity pressure as 建议整理上下文, never as a service fault', () => {
    render(
      <ComposerStatusDetails
        summary={{ ...baseSummary, contextService: 'capacity' }}
      />,
    );

    // 诊断 §4.2：75% 有效输入压力是容量信号，不是网络/API/服务故障。
    expect(screen.getByText('建议整理上下文')).toBeTruthy();
    expect(screen.queryByText('上下文服务异常')).toBeNull();
    expect(screen.queryByText('异常')).toBeNull();
  });

  it('labels the window-minus-effective difference as an unknown remainder, not 预留输出', () => {
    render(
      <ComposerStatusDetails
        summary={{
          ...baseSummary,
          token: {
            used: 463_800,
            limit: 1_000_000,
            effectiveLimit: 606_784,
            percentage: 76.4,
          },
        }}
      />,
    );

    // 诊断 §4.4：这个差值还可能来自 Provider 输入上限/安全余量，来源未知时不许猜。
    expect(screen.getByTestId('unavailable-for-input-label').textContent).toBe(
      '不可用于输入（来源未知）',
    );
    expect(screen.queryByText('预留输出')).toBeNull();
  });

  it('renders the this-turn timing panel inside 本轮摘要 and never fakes 0', () => {
    render(
      <ComposerStatusDetails
        summary={{
          ...baseSummary,
          turnTimings: { completedMs: 2500, providerTtftMs: null },
          usage: { promptTokens: 1234 },
        }}
      />,
    );

    expect(screen.getByText('本轮摘要')).toBeTruthy();
    expect(screen.getByText('本轮耗时')).toBeTruthy();
    expect(screen.getByText('2.5 s')).toBeTruthy();
    expect(screen.getByText('Provider 首包')).toBeTruthy();
    expect(screen.getAllByText('未采集').length).toBeGreaterThan(0);
    expect(screen.queryByText('0 ms')).toBeNull();
    expect(screen.queryByText('0 次')).toBeNull();
  });
});
