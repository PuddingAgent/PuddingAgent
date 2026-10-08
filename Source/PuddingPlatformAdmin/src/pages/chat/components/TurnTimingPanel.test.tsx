// ── TurnTimingPanel 单测：三态纪律（未采集 ≠ 0）是本面板的核心断言 ──────────
import { render, screen, within } from '@testing-library/react';
import * as React from 'react';
import TurnTimingPanel, {
  formatRequestTrim,
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
      'compaction',
      'request-trim',
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

describe('TurnTimingPanel 请求副本裁剪（F4/F5 接缝）', () => {
  it('keeps the 未采集 / 0 / 有裁剪 三态，且明说完整会话记录仍保留', () => {
    expect(formatRequestTrim(undefined, undefined)).toBe(NOT_COLLECTED_TEXT);
    expect(formatRequestTrim(null, 3)).toBe(NOT_COLLECTED_TEXT);
    // 真的没裁剪是好事实，不许折叠成「未采集」。
    expect(formatRequestTrim(0, 0)).toBe('0 条');
    expect(formatRequestTrim(91, 15)).toBe('91 条（15 轮 · 完整会话记录仍保留）');
    expect(formatRequestTrim(7, undefined)).toBe('7 条（完整会话记录仍保留）');
  });

  it('renders 压缩耗时 与 请求裁剪 两行，且不把裁剪说成已压缩', () => {
    render(
      <TurnTimingPanel
        timings={{
          completedMs: 30_000,
          compactionMs: 20_760,
          compactionAttempts: 1,
          deferredSoftCompactions: 0,
          appliedCompactions: 1,
          requestTrimmedMessages: 76,
          requestTrimmedRounds: 3,
        }}
      />,
    );

    expect(rowText('compaction')).toContain('20.8 s');
    expect(rowText('request-trim')).toContain('76 条');
    expect(rowText('request-trim')).toContain('3 轮');
    expect(rowText('request-trim')).toContain('完整会话记录仍保留');
    // 请求副本裁剪绝不能被读成「历史被删/已压缩」。
    expect(rowText('request-trim')).not.toContain('已压缩');
  });

  it('renders 0 条 when the turn really never trimmed', () => {
    render(<TurnTimingPanel timings={{ requestTrimmedMessages: 0, requestTrimmedRounds: 0 }} />);

    expect(rowText('request-trim')).toContain('0 条');
    expect(rowText('request-trim')).not.toContain(NOT_COLLECTED_TEXT);
  });
});
