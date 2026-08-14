// ── ToolCallRow 测试（P1-1，对齐 deepseek-harness D5 ToolRow）─────────────
import { fireEvent, render } from '@testing-library/react';
import * as React from 'react';
import type { TimelineItem } from '../types';
import ToolCallRowList, { buildToolCallRows } from './ToolCallRow';

/** 收集 antd-style（emotion/cssinjs）注入到 <style> 的 CSS 文本，用于断言 token/keyframes 引用 */
const injectedCssText = (): string =>
  Array.from(document.querySelectorAll('style'))
    .map((el) => el.textContent ?? '')
    .join('\n');

const makeToolCall = (
  id: string,
  name: string,
  args = '{}',
  extra: Partial<TimelineItem> = {},
): TimelineItem => ({
  id,
  type: 'tool_call',
  name,
  arguments: args,
  timestamp: 1000,
  collapsed: false,
  ...extra,
});

const makeToolResult = (
  id: string,
  name: string,
  output: string,
  extra: Partial<TimelineItem> = {},
): TimelineItem => ({
  id,
  type: 'tool_result',
  name,
  output,
  timestamp: 2000,
  collapsed: false,
  ...extra,
});

describe('ToolCallRowList', () => {
  it('renders nothing when processItems is empty', () => {
    const { container } = render(<ToolCallRowList items={[]} />);
    expect(container.querySelector('[data-testid="toolcall-list"]')).toBeNull();
    expect(container.querySelector('[data-testid="toolcall-row"]')).toBeNull();
  });

  it('ignores thinking / subagent items and only builds rows from tool_call', () => {
    const thinking: TimelineItem = {
      id: 't1',
      type: 'thinking',
      text: 'thinking text',
      timestamp: 900,
      collapsed: false,
    };
    const rows = buildToolCallRows([
      thinking,
      makeToolCall('c1', 'shell', '{"command":"git status"}'),
      makeToolResult('r1', 'shell', 'On branch master'),
    ]);
    expect(rows).toHaveLength(1);
    expect(rows[0].id).toBe('c1');
    expect(rows[0].status).toBe('done');
  });

  it('pairs tool_call with the next same-name tool_result (single-line done summary = output first line)', () => {
    const { container } = render(
      <ToolCallRowList
        items={[
          makeToolCall('c1', 'shell', '{"command":"git status"}'),
          makeToolResult('r1', 'shell', 'On branch master'),
        ]}
      />,
    );
    const row = container.querySelector(
      '[data-testid="toolcall-row"]',
    ) as HTMLElement;
    expect(row).toBeTruthy();
    expect(row.getAttribute('data-status')).toBe('done');
    expect(row.getAttribute('aria-expanded')).toBe('false');
    expect(
      (container.querySelector('[data-testid="toolcall-title"]') as HTMLElement)
        .textContent,
    ).toBe('shell');
    expect(
      (
        container.querySelector(
          '[data-testid="toolcall-summary"]',
        ) as HTMLElement
      ).textContent,
    ).toBe('On branch master');
    expect(container.querySelector('[data-testid="toolcall-in"]')).toBeNull();
    expect(container.querySelector('[data-testid="toolcall-out"]')).toBeNull();

    // 整行点击展开 → IN/OUT 卡
    fireEvent.click(row);
    expect(row.getAttribute('aria-expanded')).toBe('true');
    const inCard = container.querySelector('[data-testid="toolcall-in"]');
    const outCard = container.querySelector('[data-testid="toolcall-out"]');
    expect(inCard).toBeTruthy();
    expect(outCard).toBeTruthy();
    expect(
      (
        container.querySelector(
          '[data-testid="toolcall-in-label"]',
        ) as HTMLElement
      ).textContent,
    ).toBe('IN');
    expect(
      (
        container.querySelector(
          '[data-testid="toolcall-out-label"]',
        ) as HTMLElement
      ).textContent,
    ).toBe('OUT');
    expect(inCard?.textContent).toContain('git status');
    expect(outCard?.textContent).toContain('On branch master');
  });

  it('falls back to an arguments summary for multi-line output (first line stays out of the default panel)', () => {
    const { container } = render(
      <ToolCallRowList
        items={[
          makeToolCall('c1', 'shell', 'git diff --stat'),
          makeToolResult(
            'r1',
            'shell',
            ['line 1', 'line 2', 'line 3'].join('\n'),
          ),
        ]}
      />,
    );
    const row = container.querySelector(
      '[data-testid="toolcall-row"]',
    ) as HTMLElement;
    expect(row.getAttribute('data-status')).toBe('done');
    const summary = container.querySelector(
      '[data-testid="toolcall-summary"]',
    ) as HTMLElement;
    expect(summary.textContent).toBe('git diff --stat');
    expect(summary.textContent).not.toContain('line 1');
    expect(container.textContent).not.toContain('line 1');

    // 展开后 OUT 卡才暴露完整输出
    fireEvent.click(row);
    const outCard = container.querySelector('[data-testid="toolcall-out"]');
    expect(outCard?.textContent).toContain('line 1');
    expect(outCard?.textContent).toContain('line 3');
  });

  it('renders error rows with red first line summary and exitCode in OUT', () => {
    const { container } = render(
      <ToolCallRowList
        items={[
          makeToolCall('c1', 'file_patch', '{"path":"a.ts"}'),
          makeToolResult(
            'r1',
            'file_patch',
            'apply_patch verification failed\npatch rejected at line 12',
            { exitCode: 2, message: 'patch mismatch' },
          ),
        ]}
      />,
    );
    const row = container.querySelector(
      '[data-testid="toolcall-row"]',
    ) as HTMLElement;
    expect(row.getAttribute('data-status')).toBe('error');
    const summary = container.querySelector(
      '[data-testid="toolcall-summary"]',
    ) as HTMLElement;
    // 错误首行经 summarizeError 摘要（非 JSON 原样截断）
    expect(summary.textContent).toBe('apply_patch verification failed');
    // error 摘要使用 error token（注入 CSS 含 --pudding-status-error）
    expect(injectedCssText()).toContain('--pudding-status-error');

    fireEvent.click(row);
    const outCard = container.querySelector('[data-testid="toolcall-out"]');
    expect(outCard).toBeTruthy();
    const errorLine = container.querySelector(
      '[data-testid="toolcall-out-error-line"]',
    ) as HTMLElement;
    expect(errorLine).toBeTruthy();
    expect(errorLine.textContent).toBe('apply_patch verification failed');
    expect(errorLine.className.length).toBeGreaterThan(0);
    // OUT 卡包含完整错误与 exit code
    expect(outCard?.textContent).toContain('patch rejected at line 12');
    expect(outCard?.textContent).toContain('exit code: 2');
  });

  it('renders unpaired tool_call as running with sweep animation and a structured arguments summary', () => {
    const { container } = render(
      <ToolCallRowList
        items={[
          makeToolCall(
            'c1',
            'search',
            '{"query":"retention policy","limit":20}',
          ),
        ]}
      />,
    );
    const row = container.querySelector(
      '[data-testid="toolcall-row"]',
    ) as HTMLElement;
    expect(row.getAttribute('data-status')).toBe('running');
    const summary = container.querySelector(
      '[data-testid="toolcall-summary"]',
    ) as HTMLElement;
    // JSON 参数经结构化摘要（查询/任务/命令），原始字段名不进默认面板
    expect(summary.textContent).toContain('retention policy');
    expect(summary.textContent).not.toContain('"query"');
    // running sweep keyframes 已注入
    expect(injectedCssText()).toContain('toolCallSweep');

    // 展开仅 IN 卡（无 OUT），原始 arguments 在 IN 卡内可见
    fireEvent.click(row);
    expect(container.querySelector('[data-testid="toolcall-in"]')).toBeTruthy();
    expect(container.querySelector('[data-testid="toolcall-out"]')).toBeNull();
    expect(
      (container.querySelector('[data-testid="toolcall-in"]') as HTMLElement)
        .textContent,
    ).toContain('retention policy');
  });

  it('pairs same-name results preferentially and keeps the leftover call running', () => {
    const rows = buildToolCallRows([
      makeToolCall('c1', 'shell', '{"command":"git status"}'),
      makeToolCall('c2', 'shell', '{"command":"git log"}'),
      makeToolResult('r1', 'shell', 'On branch master'),
    ]);
    expect(rows).toHaveLength(2);
    expect(rows[0].status).toBe('done');
    expect(rows[0].result?.id).toBe('r1');
    expect(rows[1].status).toBe('running');
    expect(rows[1].result).toBeUndefined();
  });

  it('expands via keyboard Enter / Space and collapses again', () => {
    const { container } = render(
      <ToolCallRowList
        items={[
          makeToolCall('c1', 'shell', '{"command":"git status"}'),
          makeToolResult('r1', 'shell', 'On branch master'),
        ]}
      />,
    );
    const row = container.querySelector(
      '[data-testid="toolcall-row"]',
    ) as HTMLElement;
    expect(row.getAttribute('aria-expanded')).toBe('false');
    fireEvent.keyDown(row, { key: 'Enter' });
    expect(row.getAttribute('aria-expanded')).toBe('true');
    expect(
      container.querySelector('[data-testid="toolcall-expanded"]'),
    ).toBeTruthy();
    fireEvent.keyDown(row, { key: ' ' });
    expect(row.getAttribute('aria-expanded')).toBe('false');
    expect(
      container.querySelector('[data-testid="toolcall-expanded"]'),
    ).toBeNull();
  });

  it('keeps rendering running rows under prefers-reduced-motion', () => {
    const original = window.matchMedia;
    window.matchMedia = jest.fn(
      (query: string) =>
        ({
          matches: query.includes('prefers-reduced-motion'),
          media: query,
          onchange: null,
          addListener: jest.fn(),
          removeListener: jest.fn(),
          addEventListener: jest.fn(),
          removeEventListener: jest.fn(),
          dispatchEvent: jest.fn(),
        }) as unknown as MediaQueryList,
    ) as unknown as typeof window.matchMedia;

    try {
      const { container } = render(
        <ToolCallRowList
          items={[makeToolCall('c1', 'shell', '{"command":"git status"}')]}
        />,
      );
      const row = container.querySelector(
        '[data-testid="toolcall-row"]',
      ) as HTMLElement;
      expect(row).toBeTruthy();
      expect(row.getAttribute('data-status')).toBe('running');
      // reduced-motion 降级媒体查询已注入，DOM 不崩
      expect(injectedCssText()).toContain('prefers-reduced-motion');
      fireEvent.click(row);
      expect(
        container.querySelector('[data-testid="toolcall-in"]'),
      ).toBeTruthy();
    } finally {
      window.matchMedia = original;
    }
  });
});
