import { render, waitFor } from '@testing-library/react';
import * as React from 'react';
import {
  MermaidBlock,
  __setRichBlockLoadersForTest,
  type MermaidLib,
  type PurifyLib,
} from './MermaidBlock';

/**
 * P1 富媒体：Mermaid 围栏渲染器。
 *
 * 不真的跑 mermaid（jsdom 没有布局能力），也不 mock 模块——
 * 代码里给了 `__setRichBlockLoadersForTest` 这个注入点，直接把假的库塞进去。
 * 理由是 jest 的 CommonJS 运行时**无法执行 `import()`**
 * （ERR_VM_DYNAMIC_IMPORT_CALLBACK_MISSING_FLAG），不注入就只能测到降级分支。
 *
 * 验证四件事：
 * 1) 懒加载后调用 render，并把消毒过的 SVG 插进 DOM；
 * 2) initialize 用 securityLevel=strict / suppressErrorRendering；
 * 3) 渲染失败必须降级为原文 + 原因，不能留白；
 * 4) 代码变化触发重新渲染。
 */
const diagram = 'graph TD; A-->B';

const makeMockMermaid = () => {
  const initialize = jest.fn();
  const render = jest.fn();
  return { initialize, render } as unknown as MermaidLib & {
    initialize: jest.Mock;
    render: jest.Mock;
  };
};

const makeMockPurify = () => {
  const sanitize = jest.fn((html: string) => html);
  return { sanitize } as unknown as PurifyLib & { sanitize: jest.Mock };
};

let restore: () => void = () => {};

afterEach(() => {
  restore();
});

describe('MermaidBlock', () => {
  it('渲染成功后插入消毒过的 SVG', async () => {
    const mermaid = makeMockMermaid();
    const purify = makeMockPurify();
    mermaid.render.mockResolvedValue({ svg: '<svg data-mermaid="ok"><g/></svg>' });
    restore = __setRichBlockLoadersForTest(async () => [mermaid, purify]);

    const { getByTestId } = render(<MermaidBlock code={diagram} />);

    // 先出现"正在渲染"，说明渲染是异步的，不阻塞主线程。
    expect(getByTestId('mermaid-block-pending')).toBeTruthy();

    await waitFor(() => expect(getByTestId('mermaid-block')).toBeTruthy());
    expect(getByTestId('mermaid-block').innerHTML).toContain('data-mermaid="ok"');
    // 插入前必须经过 DOMPurify，且只用 svg profile。
    expect(purify.sanitize).toHaveBeenCalledTimes(1);
    expect(purify.sanitize.mock.calls[0][1]).toEqual({
      USE_PROFILES: { svg: true, svgFilters: true },
    });
    expect(mermaid.render).toHaveBeenCalledTimes(1);
  });

  it('initialize 使用 securityLevel=strict 与 suppressErrorRendering', async () => {
    const mermaid = makeMockMermaid();
    const purify = makeMockPurify();
    mermaid.render.mockResolvedValue({ svg: '<svg/>' });
    restore = __setRichBlockLoadersForTest(async () => [mermaid, purify]);

    render(<MermaidBlock code={diagram} />);

    await waitFor(() => expect(mermaid.initialize).toHaveBeenCalled());
    const config = mermaid.initialize.mock.calls[0][0] as Record<string, unknown>;
    expect(config.securityLevel).toBe('strict');
    expect(config.startOnLoad).toBe(false);
    expect(config.suppressErrorRendering).toBe(true);
  });

  it('渲染失败时降级为原文并显示原因', async () => {
    const mermaid = makeMockMermaid();
    const purify = makeMockPurify();
    mermaid.render.mockRejectedValue(new Error('Parse error on line 1'));
    restore = __setRichBlockLoadersForTest(async () => [mermaid, purify]);

    const { getByTestId } = render(<MermaidBlock code={diagram} />);

    await waitFor(() => expect(getByTestId('mermaid-block-fallback')).toBeTruthy());
    const fallback = getByTestId('mermaid-block-fallback');
    expect(fallback.textContent).toContain('Parse error on line 1');
    // 原文必须保留，用户至少还能复制走。
    expect(fallback.textContent).toContain('graph TD; A-->B');
  });

  it('库加载失败也走降级（不只渲染错误）', async () => {
    restore = __setRichBlockLoadersForTest(async () => {
      throw new Error('chunk load failed');
    });

    const { getByTestId } = render(<MermaidBlock code={diagram} />);

    await waitFor(() => expect(getByTestId('mermaid-block-fallback')).toBeTruthy());
    expect(getByTestId('mermaid-block-fallback').textContent).toContain(
      'chunk load failed',
    );
  });

  it('代码变化会重新渲染', async () => {
    const mermaid = makeMockMermaid();
    const purify = makeMockPurify();
    mermaid.render.mockResolvedValue({ svg: '<svg data-v="1"/>' });
    restore = __setRichBlockLoadersForTest(async () => [mermaid, purify]);

    const { rerender, getByTestId } = render(<MermaidBlock code={diagram} />);
    await waitFor(() => expect(getByTestId('mermaid-block')).toBeTruthy());

    rerender(<MermaidBlock code={'graph LR; X-->Y'} />);
    await waitFor(() => expect(mermaid.render).toHaveBeenCalledTimes(2));
    expect(mermaid.render.mock.calls[1][1]).toBe('graph LR; X-->Y');
  });
});
