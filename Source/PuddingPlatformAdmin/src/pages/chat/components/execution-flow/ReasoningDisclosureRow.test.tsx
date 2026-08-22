// ── CU-06: ReasoningDisclosureRow 行式推理披露测试 ─────────────────────────
// 验收（split-plan CU-06 验收 1/2/3/4/5）：
//  - running：折叠行显示最新非空行（§10 场景 2）；isCurrent 切换后摘要稳定为首行（场景 3）
//  - completed：摘要稳定为首条非空行
//  - 无 payload（空数组/全空白行）不渲染、不伪造内容（验收 3）
//  - 展开态：完整文本保留换行（可审计）、内容区最大高度 320px 内部滚动、复制按钮
//  - 去掉门控后整个 turn 保持同一行：isCurrent=false 时行仍渲染（验收 4 组件层面）
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import * as React from 'react';
import { ReasoningDisclosureRow } from './ReasoningDisclosureRow';

/** 收集 antd-style（emotion/cssinjs）注入到 <style> 的 CSS 文本 */
const injectedCssText = (): string =>
  Array.from(document.querySelectorAll('style'))
    .map((el) => el.textContent ?? '')
    .join('\n');

describe('ReasoningDisclosureRow', () => {
  const lines = [
    { id: 't1', text: '第一行' },
    { id: 't2', text: '第二行' },
    { id: 't3', text: '第三行' },
  ];

  it('running: 折叠行显示最新非空行，不显示早期行', () => {
    render(<ReasoningDisclosureRow lines={lines} isCurrent />);
    const row = screen.getByTestId('reasoning-disclosure-row');
    // 复用共享行式 chrome：可点击、aria-expanded、键盘语义
    expect(row.getAttribute('role')).toBe('button');
    expect(row.getAttribute('tabindex')).toBe('0');
    expect(row.getAttribute('aria-expanded')).toBe('false');
    expect(screen.getByText('思考')).toBeTruthy();
    expect(screen.getByText('第三行')).toBeTruthy();
    expect(screen.queryByText('第一行')).toBeNull();
    expect(screen.queryByText('第二行')).toBeNull();
  });

  it('running: 空白行被过滤，最新非空行优先', () => {
    render(
      <ReasoningDisclosureRow
        lines={[
          { id: 'a', text: '   ' },
          { id: 'b', text: '  有内容  ' },
        ]}
        isCurrent
      />,
    );
    expect(screen.getByText('有内容')).toBeTruthy();
  });

  it('completed: 摘要稳定为首条非空行', () => {
    render(
      <ReasoningDisclosureRow
        lines={[
          { id: 't1', text: '首行' },
          { id: 't2', text: '尾行' },
        ]}
        isCurrent={false}
      />,
    );
    expect(screen.getByText('首行')).toBeTruthy();
    expect(screen.queryByText('尾行')).toBeNull();
  });

  it('completed 不卸载：去掉 isBeforeFirstToken 门控后，行在终态仍渲染（验收 4 组件层面）', () => {
    const { rerender } = render(<ReasoningDisclosureRow lines={lines} isCurrent />);
    expect(screen.getByTestId('reasoning-disclosure-row')).toBeTruthy();
    rerender(<ReasoningDisclosureRow lines={lines} isCurrent={false} />);
    expect(screen.getByTestId('reasoning-disclosure-row')).toBeTruthy();
    expect(screen.getByText('第一行')).toBeTruthy();
  });

  it('无 payload 不渲染：空数组与全空白行均不渲染、不伪造内容（验收 3）', () => {
    const { rerender } = render(<ReasoningDisclosureRow lines={[]} />);
    expect(screen.queryByTestId('reasoning-disclosure-row')).toBeNull();
    expect(screen.queryByText('思考')).toBeNull();
    rerender(
      <ReasoningDisclosureRow
        lines={[
          { id: 'x', text: '  \n\t ' },
          { id: 'y', text: '' },
        ]}
      />,
    );
    expect(screen.queryByTestId('reasoning-disclosure-row')).toBeNull();
  });

  it('展开态：完整文本保留换行、含全部行；Enter 可折叠（验收 1/2）', () => {
    render(<ReasoningDisclosureRow lines={lines} />);
    fireEvent.click(screen.getByTestId('reasoning-disclosure-row'));
    expect(
      screen.getByTestId('reasoning-disclosure-row').getAttribute(
        'aria-expanded',
      ),
    ).toBe('true');
    const body = screen.getByTestId('reasoning-disclosure-body');
    expect(body.textContent).toContain('第一行');
    expect(body.textContent).toContain('第二行');
    expect(body.textContent).toContain('第三行');
    // 保留换行（可审计原文；join('\n\n') 产生空行分隔）
    expect(body.textContent).toContain('\n\n');
    // 键盘折叠
    fireEvent.keyDown(screen.getByTestId('reasoning-disclosure-row'), {
      key: 'Enter',
    });
    expect(
      screen.getByTestId('reasoning-disclosure-row').getAttribute(
        'aria-expanded',
      ),
    ).toBe('false');
    expect(screen.queryByTestId('reasoning-disclosure-body')).toBeNull();
  });

  it('展开态内容区：最大高度 320px、超出内部滚动（注入 CSS 断言）', () => {
    render(<ReasoningDisclosureRow lines={lines} />);
    fireEvent.click(screen.getByTestId('reasoning-disclosure-row'));
    const css = injectedCssText();
    expect(css).toContain('max-height:320px');
    const body = screen.getByTestId('reasoning-disclosure-body');
    const style = window.getComputedStyle(body);
    expect(style.overflow).toBe('auto');
  });

  it('复制按钮：点击调用 clipboard.writeText 写入完整文本并显示已复制', async () => {
    const writeText = jest.fn().mockResolvedValue(undefined);
    Object.assign(navigator, { clipboard: { writeText } });
    render(<ReasoningDisclosureRow lines={lines} />);
    fireEvent.click(screen.getByTestId('reasoning-disclosure-row'));
    fireEvent.click(screen.getByTestId('reasoning-copy'));
    await waitFor(() =>
      expect(writeText).toHaveBeenCalledWith('第一行\n\n第二行\n\n第三行'),
    );
    await waitFor(() => expect(screen.getByText('已复制')).toBeTruthy());
  });
});
