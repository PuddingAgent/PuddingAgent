// ── ModelRetryRow 测试（P1-2，对齐 deepseek-harness D3 ModelRetryItem）─────────────
// 覆盖：canonical 类型/格式识别、普通思考 retry 误报、(n/max) 提取、message 优先原因、
// 无 retry 不渲染、单条渲染、多条取最新 + 展开历次时间线、键盘 Enter/Space、reduced-motion 不崩。
import { fireEvent, render } from '@testing-library/react';
import * as React from 'react';
import type { TimelineItem } from '../types';
import ModelRetryRow, {
  buildModelRetryEntries,
  isModelRetryItem,
  parseRetryRatio,
} from './ModelRetryRow';

/** 收集 antd-style（emotion/cssinjs）注入到 <style> 的 CSS 文本，用于断言 token 引用 */
const injectedCssText = (): string =>
  Array.from(document.querySelectorAll('style'))
    .map((el) => el.textContent ?? '')
    .join('\n');

/** 后端 DirectLlmClient 重试 summary 投影后的 TimelineItem（type=subconscious_step，text=summary，message=底层错误） */
const makeRetryItem = (
  id: string,
  text: string,
  extra: Partial<TimelineItem> = {},
): TimelineItem => ({
  id,
  type: 'subconscious_step',
  text,
  timestamp: 1000,
  collapsed: false,
  ...extra,
});

describe('isModelRetryItem（嗅探规则）', () => {
  it('匹配后端 LLM call retry 文本形态', () => {
    expect(isModelRetryItem(makeRetryItem('a', 'LLM call retry 2/3.'))).toBe(
      true,
    );
    expect(
      isModelRetryItem(
        makeRetryItem('b', 'LLM stream retry before first delta 1/3.'),
      ),
    ).toBe(true);
  });

  it('普通 subconscious_step 即使含 retry 也不命中', () => {
    expect(
      isModelRetryItem(makeRetryItem('c', 'provider reset, will retry.')),
    ).toBe(false);
  });

  it('普通 thinking 中的 task_get retry 文本不命中', () => {
    expect(
      isModelRetryItem({
        id: 'thinking-retry',
        type: 'thinking',
        text: '-fetch (task_get) and retry',
        timestamp: 1,
        collapsed: true,
      }),
    ).toBe(false);
    expect(
      isModelRetryItem({
        id: 'thinking-canonical-lookalike',
        type: 'thinking',
        text: 'LLM call retry 2/3.',
        timestamp: 2,
        collapsed: true,
      }),
    ).toBe(false);
  });

  it('普通 subconscious_step 文本不命中', () => {
    expect(isModelRetryItem(makeRetryItem('d', '正在压缩上下文…'))).toBe(false);
    expect(isModelRetryItem(makeRetryItem('e', '模型正常生成回复'))).toBe(false);
  });

  it('tool_call / tool_result 即使文本含 retry 也不命中', () => {
    const toolCall: TimelineItem = {
      id: 'tc',
      type: 'tool_call',
      name: 'shell',
      arguments: '{}',
      text: 'LLM call retry 2/3.',
      timestamp: 1,
      collapsed: false,
    };
    const toolResult: TimelineItem = {
      id: 'tr',
      type: 'tool_result',
      name: 'shell',
      output: 'retry happened',
      timestamp: 2,
      collapsed: false,
    };
    expect(isModelRetryItem(toolCall)).toBe(false);
    expect(isModelRetryItem(toolResult)).toBe(false);
  });
});

describe('parseRetryRatio / buildModelRetryEntries（纯函数）', () => {
  it('从重试标签提取 (n/max)', () => {
    expect(parseRetryRatio('LLM call retry 2/3.')).toEqual({
      attempt: 2,
      maxRetries: 3,
    });
    expect(
      parseRetryRatio('LLM stream retry before first delta 1/3.'),
    ).toEqual({ attempt: 1, maxRetries: 3 });
    expect(parseRetryRatio('no ratio here')).toBeNull();
    expect(parseRetryRatio('provider reset, will retry 2/3')).toBeNull();
    expect(parseRetryRatio('LLM call retry 4/3.')).toBeNull();
  });

  it('原因摘要优先取 message（经 summarizeError），title 挂全量', () => {
    const entries = buildModelRetryEntries([
      makeRetryItem('a', 'LLM call retry 1/3.', {
        message: '{"message":"connection reset","code":503}',
        timestamp: 1,
      }),
    ]);
    expect(entries).toHaveLength(1);
    expect(entries[0].attempt).toBe(1);
    expect(entries[0].maxRetries).toBe(3);
    expect(entries[0].reasonSummary).toBe('connection reset');
    expect(entries[0].reasonFull).toBe(
      '{"message":"connection reset","code":503}',
    );
  });

  it('无 message 时回退 text 作为原因', () => {
    const entries = buildModelRetryEntries([
      makeRetryItem('a', 'LLM call retry 2/3.'),
    ]);
    expect(entries[0].reasonSummary).toContain('LLM call retry 2/3.');
  });

  it('实时 subconscious_step 只有 message 时仍识别 canonical 次数', () => {
    const entries = buildModelRetryEntries([
      makeRetryItem('live', '', {
        message: '🧠 LLM stream retry before first delta 1/3.',
      }),
    ]);
    expect(entries).toHaveLength(1);
    expect(entries[0]).toMatchObject({ attempt: 1, maxRetries: 3 });
  });

  it('按时间升序排列（折叠行取末位 = 最新）', () => {
    const entries = buildModelRetryEntries([
      makeRetryItem('late', 'LLM call retry 2/3.', { timestamp: 200 }),
      makeRetryItem('early', 'LLM call retry 1/3.', { timestamp: 100 }),
    ]);
    expect(entries.map((entry) => entry.id)).toEqual(['early', 'late']);
  });
});

describe('ModelRetryRow 条件渲染', () => {
  it('无 retry 条目时不渲染', () => {
    const { container } = render(<ModelRetryRow items={[]} />);
    expect(container.querySelector('[data-testid="model-retry-list"]')).toBeNull();
    expect(container.querySelector('[data-testid="model-retry-row"]')).toBeNull();
  });

  it('processItems 全是普通条目时也不渲染', () => {
    const { container } = render(
      <ModelRetryRow
        items={[
          makeRetryItem('a', '正在压缩上下文…'),
          {
            id: 'b',
            type: 'thinking',
            text: '用户问的是商用密码应用安全性评估。',
            timestamp: 1,
            collapsed: true,
          },
        ]}
      />,
    );
    expect(container.querySelector('[data-testid="model-retry-list"]')).toBeNull();
  });

  it('单条 retry：StateDot(warning) + 模型重试中 + (n/max) + 原因摘要', () => {
    const { container } = render(
      <ModelRetryRow
        active
        items={[
          makeRetryItem('a', 'LLM call retry 2/3.', {
            message: 'connection reset by peer',
            timestamp: 1,
          }),
        ]}
      />,
    );
    const list = container.querySelector('[data-testid="model-retry-list"]');
    expect(list).toBeTruthy();
    const row = container.querySelector(
      '[data-testid="model-retry-row"]',
    ) as HTMLElement;
    expect(row.getAttribute('aria-expanded')).toBe('false');
    // StateDot(warning)：真实组件注入 warning token
    expect(injectedCssText()).toContain('--pudding-status-warning');
    expect(
      (
        container.querySelector(
          '[data-testid="model-retry-title"]',
        ) as HTMLElement
      ).textContent,
    ).toBe('模型重试中');
    expect(
      (
        container.querySelector(
          '[data-testid="model-retry-ratio"]',
        ) as HTMLElement
      ).textContent,
    ).toBe('(2/3)');
    const summary = container.querySelector(
      '[data-testid="model-retry-summary"]',
    ) as HTMLElement;
    expect(summary.textContent).toBe('connection reset by peer');
    expect(summary.getAttribute('title')).toBe('connection reset by peer');
    // 折叠态不渲染展开时间线
    expect(container.querySelector('[data-testid="model-retry-expanded"]')).toBeNull();
  });

  it('Turn 终态或 retry 之后已有新过程事实时显示模型已重试', () => {
    const retry = makeRetryItem('retry', 'LLM call retry 1/3.', {
      timestamp: 100,
    });
    const { container, rerender } = render(
      <ModelRetryRow active={false} items={[retry]} />,
    );
    expect(
      container.querySelector('[data-testid="model-retry-title"]')?.textContent,
    ).toBe('模型已重试');

    rerender(
      <ModelRetryRow
        active
        items={[
          retry,
          {
            id: 'later-thinking',
            type: 'thinking',
            text: '继续处理任务',
            timestamp: 200,
            collapsed: true,
          },
        ]}
      />,
    );
    expect(
      container.querySelector('[data-testid="model-retry-title"]')?.textContent,
    ).toBe('模型已重试');
  });

  it('多条 retry：折叠行取最新一条，展开列出历次重试时间线', () => {
    const { container } = render(
      <ModelRetryRow
        items={[
          makeRetryItem('first', 'LLM call retry 1/3.', {
            message: 'timeout 1',
            timestamp: 100,
          }),
          makeRetryItem('second', 'LLM call retry 2/3.', {
            message: 'timeout 2',
            timestamp: 200,
          }),
          makeRetryItem(
            'third',
            'LLM stream retry before first delta 3/3.',
            { message: 'timeout 3', timestamp: 300 },
          ),
        ]}
      />,
    );
    // 折叠行 = 最新（3/3 + timeout 3）
    expect(
      (
        container.querySelector(
          '[data-testid="model-retry-ratio"]',
        ) as HTMLElement
      ).textContent,
    ).toBe('(3/3)');
    expect(
      (
        container.querySelector(
          '[data-testid="model-retry-summary"]',
        ) as HTMLElement
      ).textContent,
    ).toBe('timeout 3');

    fireEvent.click(
      container.querySelector('[data-testid="model-retry-row"]') as HTMLElement,
    );
    const timelineRows = container.querySelectorAll(
      '[data-testid="model-retry-timeline-row"]',
    );
    expect(timelineRows).toHaveLength(3);
    expect(timelineRows[0].textContent).toContain('(1/3)');
    expect(timelineRows[0].textContent).toContain('timeout 1');
    expect(timelineRows[1].textContent).toContain('(2/3)');
    expect(timelineRows[1].textContent).toContain('timeout 2');
    expect(timelineRows[2].textContent).toContain('(3/3)');
    expect(timelineRows[2].textContent).toContain('timeout 3');
    // 时间线每行有时间戳
    expect(timelineRows[0].querySelector('span')).toBeTruthy();
  });

  it('键盘可达：Enter / Space 展开与收起', () => {
    const { container } = render(
      <ModelRetryRow
        items={[
          makeRetryItem('a', 'LLM call retry 1/3.', {
            message: 'timeout',
            timestamp: 1,
          }),
        ]}
      />,
    );
    const row = container.querySelector(
      '[data-testid="model-retry-row"]',
    ) as HTMLElement;
    expect(row.getAttribute('aria-expanded')).toBe('false');
    fireEvent.keyDown(row, { key: 'Enter' });
    expect(row.getAttribute('aria-expanded')).toBe('true');
    expect(container.querySelector('[data-testid="model-retry-expanded"]')).toBeTruthy();
    fireEvent.keyDown(row, { key: ' ' });
    expect(row.getAttribute('aria-expanded')).toBe('false');
    expect(container.querySelector('[data-testid="model-retry-expanded"]')).toBeNull();
  });

  it('prefers-reduced-motion 下渲染与展开不崩', () => {
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
        <ModelRetryRow
          items={[
            makeRetryItem('a', 'LLM call retry 1/3.', {
              message: 'timeout',
              timestamp: 1,
            }),
          ]}
        />,
      );
      const row = container.querySelector(
        '[data-testid="model-retry-row"]',
      ) as HTMLElement;
      expect(row).toBeTruthy();
      fireEvent.click(row);
      expect(
        container.querySelector('[data-testid="model-retry-expanded"]'),
      ).toBeTruthy();
      expect(injectedCssText()).toContain('--pudding-status-warning');
    } finally {
      window.matchMedia = original;
    }
  });
});
