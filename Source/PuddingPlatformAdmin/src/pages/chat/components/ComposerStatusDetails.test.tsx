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
});
