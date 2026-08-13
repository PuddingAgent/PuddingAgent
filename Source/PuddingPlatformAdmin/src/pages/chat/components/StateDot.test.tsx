import { render } from '@testing-library/react';
import * as React from 'react';
import StateDot, { STATE_DOT_COLOR } from './StateDot';

/**
 * 收集 antd-style（emotion/cssinjs）注入到 <style> 的 CSS 文本，
 * 用于断言组件样式真实引用了 var(--pudding-status-*) token。
 */
const injectedCssText = (): string =>
  Array.from(document.querySelectorAll('style'))
    .map((el) => el.textContent ?? '')
    .join('\n');

describe('StateDot', () => {
  it.each(['done', 'warning', 'error'] as const)(
    'renders %s as a halo+core dot referencing the status token',
    (state) => {
      const { container } = render(<StateDot state={state} />);
      const root = container.querySelector('[data-testid="state-dot"]');
      expect(root).toBeTruthy();
      expect(root?.getAttribute('aria-hidden')).toBe('true');
      expect(root?.getAttribute('data-state')).toBe(state);
      // createStyles 已挂载 hash class（antd-style cx 会把 root/状态/haloCore 合并为单个类）
      const classes = (root?.getAttribute('class') ?? '')
        .split(/\s+/)
        .filter(Boolean);
      expect(classes.length).toBeGreaterThanOrEqual(1);
      // 颜色变量引用真实注入到样式表（组件内零字面量色）
      expect(injectedCssText()).toContain(STATE_DOT_COLOR[state]);
      // halo+core 由 ::before/::after 承载，无额外子元素
      expect(container.querySelectorAll('[data-state-cell]')).toHaveLength(0);
    },
  );

  it('renders ongoing as an 8-cell clockwise pixel chase referencing the running token', () => {
    const { container } = render(<StateDot state="ongoing" />);
    const root = container.querySelector('[data-testid="state-dot"]');
    expect(root?.getAttribute('aria-hidden')).toBe('true');
    expect(root?.getAttribute('data-state')).toBe('ongoing');
    expect(injectedCssText()).toContain(STATE_DOT_COLOR.ongoing);
    const cells = Array.from(
      container.querySelectorAll('[data-state-cell]'),
    ) as HTMLElement[];
    expect(cells).toHaveLength(8);
    // 负 animation-delay 相位：顺时针 8 cell 依次落后 0.125s
    expect(cells[0].style.animationDelay).toBe('0s');
    expect(cells[1].style.animationDelay).toBe('-0.125s');
    expect(cells[7].style.animationDelay).toBe('-0.875s');
    // 像素追逐 keyframes 已注入（steps/flat keyframe 无补间）
    expect(injectedCssText()).toContain('puddingPixelChase');
  });

  it('applies a custom size via the --pudding-state-dot-size CSS variable', () => {
    const { container } = render(<StateDot state="done" size={16} />);
    const root = container.querySelector(
      '[data-testid="state-dot"]',
    ) as HTMLElement;
    expect(root.style.getPropertyValue('--pudding-state-dot-size')).toBe(
      '16px',
    );
  });

  it('keeps rendering every state under prefers-reduced-motion', () => {
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
      for (const state of ['done', 'warning', 'ongoing', 'error'] as const) {
        const { container } = render(<StateDot state={state} />);
        const root = container.querySelector('[data-testid="state-dot"]');
        expect(root).toBeTruthy();
        expect(root?.getAttribute('data-state')).toBe(state);
        expect(root?.getAttribute('aria-hidden')).toBe('true');
        if (state === 'ongoing') {
          // reduced-motion 下像素网格退化为静态单点：DOM 结构不崩，cells 仍渲染（动画由 CSS 关闭）
          expect(
            container.querySelectorAll('[data-state-cell]'),
          ).toHaveLength(8);
        }
      }
      // 降级媒体查询已注入
      expect(injectedCssText()).toContain('prefers-reduced-motion');
    } finally {
      window.matchMedia = original;
    }
  });
});
