// ── TurnTimingPanel 单测：三态纪律（未采集 ≠ 0）是本面板的核心断言 ──────────
import { render, screen, within } from '@testing-library/react';
import * as React from 'react';
import TurnTimingPanel, {
  formatTtftSource,
  formatTtftSplit,
  formatTimingCount,
  formatTimingMs,
  formatTimingTokens,
  NOT_COLLECTED_TEXT,
} from './TurnTimingPanel';

jest.mock('../styles', () => ({
  useChatStyles: () => ({
    styles: {
      composerStatusDetailRow: 'composerStatusDetailRow',
      composerStatusDetailLabel: 'composerStatusDetailLabel',
      composerStatusDetailValue: 'composerStatusDetailValue',
    },
  }),
}));

const rowText = (key: string): string =>
  screen.getByTestId(`turn-timing-${key}`).textContent ?? '';

describe('TurnTimingPanel', () => {
  it('renders nothing before any turn timing or usage exists', () => {
    const { container } = render(<TurnTimingPanel />);
    expect(container.textContent).toBe('');
  });

  it('renders 未采集 for every absent field and never 0', () => {
    render(<TurnTimingPanel timings={{}} />);

    for (const key of [
      'completed',
      'model',
      'tool',
      'ttft',
      'model-calls',
      'tool-calls',
      'input-tokens',
      'output-tokens',
    ]) {
      expect(within(screen.getByTestId(`turn-timing-${key}`)).getAllByText(NOT_COLLECTED_TEXT).length)
        .toBeGreaterThan(0);
    }
    // 关键回归：缺席不得被渲染成 0。
    expect(screen.queryByText('0 ms')).toBeNull();
    expect(screen.queryByText('0 次')).toBeNull();
    expect(screen.queryByText('0')).toBeNull();
  });

  it('renders 未采集 for null/NaN fields while a real 0 stays 0 ms / 0 次', () => {
    render(
      <TurnTimingPanel
        timings={{
          completedMs: 0,
          modelMs: null,
          toolMs: Number.NaN,
          modelCalls: 0,
          providerTtftMs: null,
        }}
        usage={{ promptTokens: 12345, completionTokens: 0 }}
      />,
    );

    expect(rowText('completed')).toContain('0 ms');
    expect(rowText('model')).toContain(NOT_COLLECTED_TEXT);
    expect(rowText('tool')).toContain(NOT_COLLECTED_TEXT);
    expect(rowText('model-calls')).toContain('0 次');
    expect(rowText('tool-calls')).toContain(NOT_COLLECTED_TEXT);
    expect(rowText('input-tokens')).toContain('12,345');
    expect(rowText('output-tokens')).toContain('0');
    expect(rowText('output-tokens')).not.toContain(NOT_COLLECTED_TEXT);
  });

  it('renders real durations, TTFT source and the reasoning/content split', () => {
    render(
      <TurnTimingPanel
        timings={{
          completedMs: 2200,
          modelMs: 400,
          toolMs: 3200,
          providerTtftMs: 1200,
          providerTtftSource: 'provider_dispatch',
          providerFirstReasoningMs: 900,
          providerFirstContentMs: 1200,
          modelCalls: 2,
          toolCalls: 1,
        }}
      />,
    );

    expect(rowText('completed')).toContain('2.2 s');
    expect(rowText('model')).toContain('400 ms');
    expect(rowText('tool')).toContain('3.2 s');
    expect(rowText('ttft')).toContain('1.2 s（后端计时）');
    expect(rowText('ttft-split')).toContain('推理 900 ms · 内容 1.2 s');
    expect(rowText('model-calls')).toContain('2 次');
    expect(rowText('tool-calls')).toContain('1 次');
  });
});

describe('TurnTimingPanel formatters', () => {
  it('keeps the 未采集 / 0 distinction in every formatter', () => {
    expect(formatTimingMs(null)).toBe(NOT_COLLECTED_TEXT);
    expect(formatTimingMs(undefined)).toBe(NOT_COLLECTED_TEXT);
    expect(formatTimingMs(Number.POSITIVE_INFINITY)).toBe(NOT_COLLECTED_TEXT);
    expect(formatTimingMs(0)).toBe('0 ms');
    expect(formatTimingMs(999)).toBe('999 ms');
    expect(formatTimingMs(1500)).toBe('1.5 s');

    expect(formatTimingCount(undefined)).toBe(NOT_COLLECTED_TEXT);
    expect(formatTimingCount(0)).toBe('0 次');

    expect(formatTimingTokens(undefined)).toBe(NOT_COLLECTED_TEXT);
    expect(formatTimingTokens(0)).toBe('0');
  });

  it('labels only the known TTFT sources', () => {
    expect(formatTtftSource('provider_dispatch')).toBe('后端计时');
    expect(formatTtftSource('local_model_call')).toBe('本地计时');
    expect(formatTtftSource('unavailable')).toBeUndefined();
    expect(formatTtftSource(undefined)).toBeUndefined();
  });

  it('renders the split only when at least one side was collected', () => {
    expect(formatTtftSplit(undefined)).toBeUndefined();
    expect(formatTtftSplit({})).toBeUndefined();
    expect(formatTtftSplit({ providerFirstContentMs: 800 })).toBe(
      `推理 ${NOT_COLLECTED_TEXT} · 内容 800 ms`,
    );
  });
});
