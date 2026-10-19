import { fireEvent, render } from '@testing-library/react';
import * as React from 'react';
import { DraftPanel } from './DraftPanel';
import type { DraftSpec } from './spec';

/**
 * 草稿面板：单一草稿的展示与确认。
 *
 * 与 variants 一样，重点是**两条渲染路径的隔离强度**：
 * html 必须进沙箱；svg 必须过 DOMPurify。
 */
describe('DraftPanel', () => {
  it('html 草稿渲染为 sandbox="" 的 iframe', () => {
    const spec: DraftSpec = {
      v: 1,
      id: 'd1',
      kind: 'draft',
      title: '登录页草稿',
      format: 'html',
      source: '<div style="padding:12px">hello</div>',
    };

    const { getByTestId } = render(<DraftPanel spec={spec} onSubmit={jest.fn()} />);

    const frame = getByTestId('sandboxed-html') as HTMLIFrameElement;
    expect(frame.getAttribute('sandbox')).toBe('');
    expect(frame.getAttribute('srcdoc')).toContain('hello');
    expect(frame.getAttribute('title')).toBe('登录页草稿');
  });

  it('svg 草稿经 DOMPurify：<script> 不进 DOM', () => {
    const spec: DraftSpec = {
      v: 1,
      id: 'd2',
      kind: 'draft',
      format: 'svg',
      source: '<svg viewBox="0 0 10 10"><script>alert(1)</script><circle cx="5" cy="5" r="4"/></svg>',
    };

    const { getByTestId } = render(<DraftPanel spec={spec} onSubmit={jest.fn()} />);

    const holder = getByTestId('draft-svg');
    expect(holder.innerHTML).toContain('circle');
    expect(holder.innerHTML.toLowerCase()).not.toContain('script');
  });

  it('确认按钮回传 draft 提交', () => {
    const onSubmit = jest.fn();
    const spec: DraftSpec = {
      v: 1,
      id: 'd3',
      kind: 'draft',
      format: 'html',
      source: '<div/>',
    };

    const { getByTestId } = render(<DraftPanel spec={spec} onSubmit={onSubmit} />);
    fireEvent.click(getByTestId('panel-draft-submit'));

    expect(onSubmit).toHaveBeenCalledTimes(1);
    expect(onSubmit.mock.calls[0][0]).toMatchObject({ panelId: 'd3', kind: 'draft' });
  });

  it('disabled 时不能提交', () => {
    const onSubmit = jest.fn();
    const spec: DraftSpec = {
      v: 1,
      id: 'd4',
      kind: 'draft',
      format: 'html',
      source: '<div/>',
    };

    const { getByTestId } = render(
      <DraftPanel spec={spec} onSubmit={onSubmit} disabled />,
    );
    expect((getByTestId('panel-draft-submit') as HTMLButtonElement).disabled).toBe(true);
  });
});
