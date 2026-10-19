import { fireEvent, render } from '@testing-library/react';
import * as React from 'react';
import { VariantsPanel } from './VariantsPanel';
import type { VariantsSpec } from './spec';

/**
 * 多方案对比：布局、选中、两条渲染路径的隔离强度。
 *
 * 最重要的两条断言不是"能渲染"，而是：
 * 1) html 变体走 `sandbox=""` 的 iframe（不是内联 DOM）；
 * 2) svg 变体经过 DOMPurify，`<script>` 不进 DOM。
 */
const spec = (over: Partial<VariantsSpec> = {}): VariantsSpec => ({
  v: 1,
  id: 'panel-v',
  kind: 'variants',
  layout: 3,
  variants: [
    { id: 'A', title: '极简', html: '<div style="padding:8px">A</div>' },
    {
      id: 'B',
      title: '品牌',
      svg: '<svg viewBox="0 0 10 10"><rect width="10" height="10"/></svg>',
    },
    { id: 'C', title: '引导', html: '<div>C</div>' },
  ],
  ...over,
});

describe('VariantsPanel', () => {
  it('按 layout 渲染网格与全部方案', () => {
    const { getByTestId, queryByTestId } = render(
      <VariantsPanel spec={spec()} onSubmit={jest.fn()} />,
    );

    const grid = getByTestId('variants-grid');
    expect(grid.style.gridTemplateColumns).toContain('repeat(3');

    expect(queryByTestId('variant-A')).toBeTruthy();
    expect(queryByTestId('variant-B')).toBeTruthy();
    expect(queryByTestId('variant-C')).toBeTruthy();
  });

  it('html 变体渲染为 sandbox="" 的 iframe（不内联进宿主 DOM）', () => {
    const { getAllByTestId } = render(
      <VariantsPanel spec={spec()} onSubmit={jest.fn()} />,
    );

    const frames = getAllByTestId('sandboxed-html') as HTMLIFrameElement[];
    // A 与 C 是 html，B 是 svg
    expect(frames).toHaveLength(2);
    for (const frame of frames) {
      expect(frame.getAttribute('sandbox')).toBe('');
      expect(frame.getAttribute('srcdoc')).toContain('<div');
    }
  });

  it('svg 变体经过 DOMPurify：<script> 不进 DOM', () => {
    const withScript = spec({
      variants: [
        {
          id: 'B',
          title: '带脚本',
          svg: '<svg viewBox="0 0 10 10"><script>alert(1)</script><rect width="10" height="10"/></svg>',
        },
      ],
      layout: 1,
    });

    const { getByTestId } = render(
      <VariantsPanel spec={withScript} onSubmit={jest.fn()} />,
    );

    const holder = getByTestId('variant-svg-B');
    expect(holder.innerHTML).toContain('rect');
    expect(holder.innerHTML.toLowerCase()).not.toContain('script');
  });

  it('点击卡片选中；提交回传所选 id', () => {
    const onSubmit = jest.fn();
    const { getByTestId } = render(
      <VariantsPanel spec={spec()} onSubmit={onSubmit} />,
    );

    fireEvent.click(getByTestId('variant-A'));
    expect(getByTestId('variant-A').getAttribute('data-selected')).toBe('true');

    fireEvent.click(getByTestId('panel-variants-submit'));
    expect(onSubmit).toHaveBeenCalledTimes(1);
    expect(onSubmit.mock.calls[0][0]).toMatchObject({
      panelId: 'panel-v',
      kind: 'variants',
      selected: ['A'],
    });
  });

  it('单选模式下再点同一张会取消，点另一张会替换', () => {
    const { getByTestId } = render(<VariantsPanel spec={spec()} onSubmit={jest.fn()} />);

    fireEvent.click(getByTestId('variant-A'));
    fireEvent.click(getByTestId('variant-C'));
    expect(getByTestId('variant-A').getAttribute('data-selected')).toBe('false');
    expect(getByTestId('variant-C').getAttribute('data-selected')).toBe('true');

    fireEvent.click(getByTestId('variant-C'));
    expect(getByTestId('variant-C').getAttribute('data-selected')).toBe('false');
  });

  it('多选模式可同时选中两张', () => {
    const { getByTestId } = render(
      <VariantsPanel spec={spec({ selectMode: 'multi' })} onSubmit={jest.fn()} />,
    );

    fireEvent.click(getByTestId('variant-A'));
    fireEvent.click(getByTestId('variant-C'));
    expect(getByTestId('variant-A').getAttribute('data-selected')).toBe('true');
    expect(getByTestId('variant-C').getAttribute('data-selected')).toBe('true');
  });

  it('未选中时提交按钮禁用', () => {
    const { getByTestId } = render(<VariantsPanel spec={spec()} onSubmit={jest.fn()} />);
    expect(
      (getByTestId('panel-variants-submit') as HTMLButtonElement).disabled,
    ).toBe(true);
  });

  it('标注按 targetId 分发到对应卡片', () => {
    const { getAllByTestId, queryAllByTestId } = render(
      <VariantsPanel
        spec={spec()}
        onSubmit={jest.fn()}
        annotations={[
          { type: 'rect', x: 0.1, y: 0.2, w: 0.3, h: 0.1, text: '太挤', targetId: 'A' },
          { type: 'note', at: { x: 0.5, y: 0.5 }, text: '整体偏商务' },
        ]}
      />,
    );

    // 两个标注都可见（note 无 targetId 会出现在所有卡片里）
    expect(getAllByTestId('annotation-layer').length).toBeGreaterThanOrEqual(3);
    expect(queryAllByTestId('annotation-note').length).toBe(3);
  });
});
