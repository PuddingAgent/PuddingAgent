import { render, screen } from '@testing-library/react';
import * as React from 'react';
import ContextUsageRing from './ContextUsageRing';
jest.mock('../styles', () => ({ useChatStyles: () => ({ styles: new Proxy({}, { get: (_, key) => String(key) }) }) }));
jest.mock('antd', () => ({
  Popover: ({ children, content }: any) => <div>{children}{content}</div>,
  Tooltip: ({ children }: any) => <>{children}</>,
}));

/** 截图样本（诊断 §4.1）：总窗口 1000.0K，有效输入 606.8K，用量 463.8K。 */
const SAMPLE_LIMIT = 1_000_000;
const SAMPLE_EFFECTIVE = 616_000;
const SAMPLE_USED = 308_000;

/** 分层六桶；合计 = tUsed，使色段之和与标题同口径。 */
const breakdown = {
  systemPrompt: 100_000,
  toolDefinitions: 50_000,
  compactionSummary: 0,
  conversation: 158_000,
  reasoning: 0,
  toolResults: 0,
};

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

describe('two explicit denominators', () => {
  it('labels total-window occupancy and effective-input pressure with their own denominators', () => {
    render(
      <ContextUsageRing
        tLimit={SAMPLE_LIMIT}
        tEffective={SAMPLE_EFFECTIVE}
        tUsed={SAMPLE_USED}
        tPct={50}
        usageConfidence="provider_reported"
      />,
    );

    // 分母一：模型窗口（308.0K / 1000.0K = 30.8%）。
    const windowRow = screen.getByTestId('context-usage-window-occupancy').textContent ?? '';
    expect(windowRow).toContain('模型窗口占用');
    expect(windowRow).toContain('30.8%');
    expect(windowRow).toContain('308.0K/1000.0K');

    // 分母二：有效输入窗口（308.0K / 616.0K = 50.0%），独立成行、自带分母。
    const pressureRow =
      screen.getByTestId('context-usage-effective-pressure').textContent ?? '';
    expect(pressureRow).toContain('有效输入压力');
    expect(pressureRow).toContain('50.0%');
    expect(pressureRow).toContain('616.0K');

    // 两者不同分母，都出现在悬浮摘要里。
    const hover = screen.getByTestId('context-usage-ring').getAttribute('aria-label') ?? '';
    expect(hover).toContain('1000.0K');
    expect(hover).toContain('616.0K');
  });

  it('never fabricates an effective-input denominator when the server did not give one', () => {
    render(
      <ContextUsageRing tLimit={SAMPLE_LIMIT} tUsed={SAMPLE_USED} tPct={30.8} usageConfidence="provider_reported" />,
    );

    // 有效输入窗口未知 ⇒ 整行不渲染；不能拿总窗口当分母冒充。
    expect(screen.queryByTestId('context-usage-effective-pressure')).toBeNull();
    const hover = screen.getByTestId('context-usage-ring').getAttribute('aria-label') ?? '';
    expect(hover).not.toContain('有效输入压力');
  });
});

describe('unavailable-for-input region is labelled by source', () => {
  const renderWithReason = (tUnavailableReason?: 'reserved_output' | 'provider_input_limit' | 'safety_margin') =>
    render(
      <ContextUsageRing
        tLimit={SAMPLE_LIMIT}
        tEffective={SAMPLE_EFFECTIVE}
        tUsed={SAMPLE_USED}
        tPct={50}
        tBreakdown={breakdown}
        tUnavailableReason={tUnavailableReason}
        usageConfidence="provider_reported"
      />,
    );

  it('shows the window-minus-effective remainder as unknown instead of guessing 预留输出', () => {
    renderWithReason();

    const legend =
      screen.getByTestId('context-usage-unavailable-legend').textContent ?? '';
    expect(legend).toContain('不可用于输入（来源未知）');
    // 没有输出预算证据时，一个字都不许叫「预留输出」。
    expect(screen.queryByText('预留输出')).toBeNull();
  });

  it('names 预留输出 only when the capacity source really says so', () => {
    renderWithReason('reserved_output');
    expect(screen.getAllByText('预留输出').length).toBeGreaterThan(0);
    expect(screen.queryByText('不可用于输入（来源未知）')).toBeNull();
  });

  it('names the other capacity sources explicitly', () => {
    renderWithReason('provider_input_limit');
    expect(screen.getAllByText('Provider 输入上限').length).toBeGreaterThan(0);
  });
});
