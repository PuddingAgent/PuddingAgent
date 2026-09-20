import { render, screen } from '@testing-library/react';
import * as React from 'react';
import ContextUsageRing from './ContextUsageRing';
jest.mock('../styles', () => ({ useChatStyles: () => ({ styles: new Proxy({}, { get: (_, key) => String(key) }) }) }));
jest.mock('antd', () => ({
  Popover: ({ children, content }: any) => <div>{children}{content}</div>,
  Tooltip: ({ children }: any) => <>{children}</>,
}));
describe('context occupancy, not compaction progress', () => {
  it('uses the total window denominator even when passed effective-window pressure', () => {
    render(<ContextUsageRing tLimit={1000000} tEffective={616000} tUsed={308000} tPct={50} usageConfidence="provider_reported" />);
    expect(screen.getByTestId('context-usage-ring').getAttribute('aria-label')).toContain('30.8%');
    expect(screen.getByTestId('context-usage-ring').getAttribute('aria-label')).toContain('非压缩进度');
  });
  it('unknown confidence cannot masquerade as provider measured usage', () => {
    render(<ContextUsageRing tLimit={1000} tUsed={500} tPct={50} />);
    expect(screen.getByTestId('context-usage-ring').getAttribute('aria-label')).toContain('估算');
  });
  it('compaction text never turns the occupancy ring into a spinner', () => {
    render(<ContextUsageRing tLimit={1000} tUsed={500} tPct={50} compactionStatus="正在压缩上下文…" usageRecordedAt="2026-09-20T03:00:00Z" />);
    expect(screen.queryByTestId('compaction-status-dot')).toBeNull();
    expect(screen.getByText('用量采样时间')).toBeTruthy();
  });
});
