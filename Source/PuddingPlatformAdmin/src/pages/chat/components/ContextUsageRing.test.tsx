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

describe('single denominator per view', () => {
  it('falls back to the total-window denominator when no provider measurement exists', () => {
    render(
      <ContextUsageRing
        tLimit={SAMPLE_LIMIT}
        tEffective={SAMPLE_EFFECTIVE}
        tUsed={SAMPLE_USED}
        tPct={50}
        usageConfidence="provider_reported"
      />,
    );

    // 没有实测输入 ⇒ 主指标仍是总窗口占用（308.0K / 1000.0K = 30.8%）。
    const windowRow = screen.getByTestId('context-usage-window-occupancy').textContent ?? '';
    expect(windowRow).toContain('模型窗口占用');
    expect(windowRow).toContain('30.8%');
    expect(windowRow).toContain('308.0K/1000.0K');

    // 「有效输入压力」独立行已删除：同一面板不得再出现第二个百分比分母。
    expect(screen.queryByTestId('context-usage-effective-pressure')).toBeNull();
    const hover = screen.getByTestId('context-usage-ring').getAttribute('aria-label') ?? '';
    expect(hover).toContain('1000.0K');
    expect(hover).not.toContain('有效输入压力');
    expect(hover).not.toContain('616.0K');
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
  const renderWithReason = (
    tUnavailableReason?:
      | 'output_reserve'
      | 'provider_input_limit'
      | 'safety_margin'
      | 'output_reserve_and_safety_margin'
      | 'fallback_output_reserve',
  ) =>
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
    renderWithReason('output_reserve');
    expect(screen.getAllByText('预留输出').length).toBeGreaterThan(0);
    expect(screen.queryByText('不可用于输入（来源未知）')).toBeNull();
  });

  it('names the other capacity sources explicitly', () => {
    renderWithReason('provider_input_limit');
    expect(screen.getAllByText('Provider 输入上限').length).toBeGreaterThan(0);
  });
});

describe('two usage sources side by side (F1)', () => {
  it('renders 上次请求实测输入 next to the prepared estimate instead of overwriting it', () => {
    render(
      <ContextUsageRing
        tLimit={1_000_000}
        tUsed={463_800}
        tPct={46.4}
        usageSource="llm_request_calibrated"
        usageConfidence="estimated"
        measuredPromptTokens={131_100}
        measuredRawTokens={71_000}
      />,
    );

    const measured = screen.getByTestId('context-usage-last-measured');
    expect(measured.textContent).toContain('上次请求实测输入');
    expect(measured.textContent).toContain('131.1K');
    expect(measured.textContent).toContain('原始估算 71.0K');
    // 两个口径同时存在：主数值（预算估算 463.8K）不得被实测值覆盖。
    expect(screen.getByText(/463\.8K/)).toBeTruthy();
  });

  it('renders nothing for the measured row when the value was not collected', () => {
    render(
      <ContextUsageRing tLimit={1_000_000} tUsed={463_800} tPct={46.4} usageConfidence="estimated" />,
    );

    expect(screen.queryByTestId('context-usage-last-measured')).toBeNull();
  });
});

describe('measured-first headline (user ruling 2026-10-23)', () => {
  it('uses provider-measured input over the effective window and drops the approximation mark', () => {
    render(
      <ContextUsageRing
        tLimit={1_000_000}
        tEffective={616_000}
        tUsed={463_800}
        tPct={46.4}
        usageConfidence="estimated"
        measuredPromptTokens={300_000}
        measuredRawTokens={140_000}
      />,
    );

    const headline = screen.getByTestId('context-usage-window-occupancy').textContent ?? '';
    // 300.0K / 616.0K = 48.7012…% → 48.7%。
    expect(headline).toContain('48.7%');
    expect(headline).toContain('已用 300.0K');
    expect(headline).toContain('有效窗口 616.0K（实测）');
    // 实测值不得被本地估算口径抢回主槽位，也不得标「≈」。
    expect(headline).not.toContain('46.4%');
    expect(headline).not.toContain('≈');
  });

  it('drops both the effective-pressure row and the token subtraction from the panel', () => {
    render(
      <ContextUsageRing
        tLimit={SAMPLE_LIMIT}
        tEffective={SAMPLE_EFFECTIVE}
        tUsed={SAMPLE_USED}
        tPct={46.4}
        tUnavailableReason="output_reserve"
        usageConfidence="provider_reported"
      />,
    );

    expect(screen.queryByTestId('context-usage-effective-pressure')).toBeNull();
    expect(screen.queryByText('有效输入压力')).toBeNull();

    const panel = screen.getByTestId('context-usage-panel').textContent ?? '';
    expect(panel).not.toContain('有效输入压力');
    // 推导式「（1000.0K − 616.0K）」是噪音，也会把第二个分母引回来。
    expect(panel).not.toMatch(/（[^）]*−[^）]*）/);
    // 但数值本身必须仍在：不可用于输入 = 1000.0K − 616.0K = 384.0K。
    expect(panel).toContain('384.0K');
  });
});

describe('ring arc shares the headline denominator', () => {
  /** 弧线圆是第二个 circle；stroke-dasharray 的第一段就是可见弧长。 */
  const arcLength = (container: HTMLElement): number => {
    const circles = container.querySelectorAll('circle');
    const raw = circles[1]?.getAttribute('stroke-dasharray') ?? '0';
    return Number.parseFloat(raw.split(' ')[0] ?? '0');
  };

  it('draws the arc from the measured ratio, never from the total-window estimate', () => {
    const measured = render(
      <ContextUsageRing
        tLimit={1_000_000}
        tEffective={616_000}
        tUsed={463_800}
        tPct={46.4}
        usageConfidence="estimated"
        measuredPromptTokens={300_000}
      />,
    );
    const measuredArc = arcLength(measured.container);
    measured.unmount();

    const estimated = render(
      <ContextUsageRing
        tLimit={1_000_000}
        tEffective={616_000}
        tUsed={463_800}
        tPct={46.4}
        usageConfidence="estimated"
      />,
    );
    const estimatedArc = arcLength(estimated.container);

    // 48.7%（实测 / 有效窗口）> 46.4%（估算 / 总窗口）⇒ 弧长必须跟着主数字放大。
    expect(measuredArc).toBeGreaterThan(estimatedArc);
    // 比值必须等于两个百分比之比：证明弧长与标题同源，而不是各算一套分母。
    // 精度取 3 位，使「弧长仍按总窗口」的回归（比值 1.0）必然取红。
    expect(measuredArc / estimatedArc).toBeCloseTo(48.7 / 46.4, 3);
  });
});
