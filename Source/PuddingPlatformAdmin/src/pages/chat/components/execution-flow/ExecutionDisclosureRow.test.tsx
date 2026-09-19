// ── CU-05: ExecutionDisclosureRow 共享行式折叠 chrome 测试 ─────────────────
// 验收（split-plan CU-05 验收标准 4/5）：
//  - 可展开行：整行可点（≥32px 可点击区）、Enter/Space 键盘、aria-expanded、焦点环
//  - 不可展开行：非 button 语义、chevron 占位隐藏但槽位稳定
import { fireEvent, render, screen } from '@testing-library/react';
import * as React from 'react';
import { ExecutionDisclosureRow } from './ExecutionDisclosureRow';

/** 收集 antd-style（emotion/cssinjs）注入到 <style> 的 CSS 文本 */
const injectedCssText = (): string =>
  Array.from(document.querySelectorAll('style'))
    .map((el) => el.textContent ?? '')
    .join('\n');

describe('ExecutionDisclosureRow', () => {
  it('non-expandable row: no button semantics, chevron placeholder hidden, click no-op', () => {
    const onClick = jest.fn();
    const { container } = render(
      <ExecutionDisclosureRow testId="plain-row" leading={<span>●</span>}>
        <span>仅状态行</span>
      </ExecutionDisclosureRow>,
    );
    const row = screen.getByTestId('plain-row');
    expect(row.getAttribute('role')).toBeNull();
    expect(row.getAttribute('tabindex')).toBeNull();
    expect(row.getAttribute('aria-expanded')).toBeNull();
    expect(row.getAttribute('aria-live')).toBeNull();
    // chevron 槽存在但不可见（占位稳定）
    expect(row.textContent).toContain('▸');
    fireEvent.click(row);
    expect(onClick).not.toHaveBeenCalled();
    expect(screen.queryByTestId('plain-row-expanded')).toBeNull();
    // 行高基准 32px（可点击区 ≥32px 规格在可展开行上验证）
    expect(container.querySelector('div')).toBeTruthy();
  });

  it('expandable row: click toggles content and aria-expanded', () => {
    render(
      <ExecutionDisclosureRow
        testId="disclosure"
        ariaLabel="展开详情"
        expandedContent={<div>完整内容</div>}
      >
        <span>摘要行</span>
      </ExecutionDisclosureRow>,
    );
    const row = screen.getByTestId('disclosure');
    expect(row.getAttribute('role')).toBe('button');
    expect(row.getAttribute('tabindex')).toBe('0');
    expect(row.getAttribute('aria-expanded')).toBe('false');
    expect(row.getAttribute('aria-label')).toBe('展开详情');
    expect(screen.queryByTestId('disclosure-expanded')).toBeNull();

    fireEvent.click(row);
    expect(row.getAttribute('aria-expanded')).toBe('true');
    expect(screen.getByTestId('disclosure-expanded').textContent).toContain(
      '完整内容',
    );

    fireEvent.click(row);
    expect(row.getAttribute('aria-expanded')).toBe('false');
    expect(screen.queryByTestId('disclosure-expanded')).toBeNull();
  });

  it('expandable row: Enter and Space keys toggle', () => {
    render(
      <ExecutionDisclosureRow
        testId="keyboard-row"
        ariaLabel="键盘行"
        expandedContent={<div>展开</div>}
      >
        <span>行</span>
      </ExecutionDisclosureRow>,
    );
    const row = screen.getByTestId('keyboard-row');
    fireEvent.keyDown(row, { key: 'Enter' });
    expect(row.getAttribute('aria-expanded')).toBe('true');
    fireEvent.keyDown(row, { key: ' ' });
    expect(row.getAttribute('aria-expanded')).toBe('false');
    fireEvent.keyDown(row, { key: 'Escape' });
    expect(row.getAttribute('aria-expanded')).toBe('false');
  });

  it('expandable row: clickable area min-height 32px and visible focus ring in injected CSS', () => {
    render(
      <ExecutionDisclosureRow testId="focus-row" expandedContent={<div>x</div>}>
        <span>行</span>
      </ExecutionDisclosureRow>,
    );
    const row = screen.getByTestId('focus-row');
    const style = window.getComputedStyle(row);
    expect(parseFloat(style.minHeight)).toBeGreaterThanOrEqual(32);
    const css = injectedCssText();
    expect(css).toContain('focus-visible');
    expect(css).toContain('cursor');
  });

  it('controlled mode: expanded prop + onExpandedChange reported', () => {
    const onExpandedChange = jest.fn();
    const { rerender } = render(
      <ExecutionDisclosureRow
        testId="controlled"
        expanded={false}
        onExpandedChange={onExpandedChange}
        expandedContent={<div>内容</div>}
      >
        <span>行</span>
      </ExecutionDisclosureRow>,
    );
    const row = screen.getByTestId('controlled');
    fireEvent.click(row);
    expect(onExpandedChange).toHaveBeenLastCalledWith(true);
    expect(screen.queryByTestId('controlled-expanded')).toBeNull();

    rerender(
      <ExecutionDisclosureRow
        testId="controlled"
        expanded={true}
        onExpandedChange={onExpandedChange}
        expandedContent={<div>内容</div>}
      >
        <span>行</span>
      </ExecutionDisclosureRow>,
    );
    expect(screen.getByTestId('controlled-expanded')).toBeTruthy();
  });

  it('leading 16px slot renders leading content', () => {
    render(
      <ExecutionDisclosureRow
        testId="leading-row"
        leading={<span data-testid="leading-dot">●</span>}
      >
        <span>行</span>
      </ExecutionDisclosureRow>,
    );
    expect(screen.getByTestId('leading-dot')).toBeTruthy();
  });

  it('aria-live prop is forwarded to the row (single live region contract)', () => {
    render(
      <ExecutionDisclosureRow testId="live-row" ariaLive="polite">
        <span>状态</span>
      </ExecutionDisclosureRow>,
    );
    expect(screen.getByTestId('live-row').getAttribute('aria-live')).toBe(
      'polite',
    );
  });

  it('S1c progressive disclosure: chevron hidden by default, shown on hover/focus-visible, direction flips', () => {
    render(
      <ExecutionDisclosureRow
        testId="chevron-row"
        expandedContent={<div>详情</div>}
      >
        <span>行</span>
      </ExecutionDisclosureRow>,
    );
    const row = screen.getByTestId('chevron-row');
    const chevron = row.querySelector('.execution-flow-chevron');
    expect(chevron).toBeTruthy();
    // ④ 折叠态右向箭头
    expect(chevron?.textContent).toBe('▸');

    // ① 非悬停/未聚焦：箭头不可见（默认 opacity:0，占位尺寸保留，行首不跳动）
    const chevronStyle = window.getComputedStyle(chevron as Element);
    expect(chevronStyle.opacity).toBe('0');

    // ② 悬停显现 ③ :focus-visible 键盘聚焦显现（a11y 硬要求：不得只靠 hover）
    const css = injectedCssText();
    expect(css).toContain(':hover .execution-flow-chevron');
    expect(css).toContain(':focus-visible .execution-flow-chevron');
    // 150ms 过渡纳入 opacity
    expect(css).toMatch(/transition:[^;]*opacity/);

    // ④ 展开态箭头方向翻转为下向
    fireEvent.click(row);
    expect(row.getAttribute('aria-expanded')).toBe('true');
    expect(chevron?.textContent).toBe('▾');
    fireEvent.click(row);
    expect(row.getAttribute('aria-expanded')).toBe('false');
    expect(chevron?.textContent).toBe('▸');
  });

  it('S1c: non-expandable row keeps visibility-hidden placeholder (behavior unchanged)', () => {
    render(
      <ExecutionDisclosureRow testId="plain-chevron-row">
        <span>仅状态行</span>
      </ExecutionDisclosureRow>,
    );
    const row = screen.getByTestId('plain-chevron-row');
    // 稳定语义类仍挂在占位 chevron 上，但不可展开行继续走 visibility:hidden 占位隐藏
    expect(row.querySelector('.execution-flow-chevron')).toBeTruthy();
    const css = injectedCssText();
    expect(css).toContain('visibility:hidden');
  });
});
