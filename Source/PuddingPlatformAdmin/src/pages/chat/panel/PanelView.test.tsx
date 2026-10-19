import { fireEvent, render, waitFor } from '@testing-library/react';
import * as React from 'react';
import { PanelView } from './PanelView';
import type { PanelSpec } from './spec';

/**
 * 面板分派与问卷交互：确认"提交一次、值正确、未填不给提交"。
 *
 * 用 antd 现有依赖渲染，不引入新库；变体/草稿尚未实现，这里断言**显式降级**
 * 而不是空白（面板占据整块区域，留白会被误认为坏了）。
 */
const surveySpec: PanelSpec = {
  v: 1,
  id: 'panel-1',
  kind: 'survey',
  title: '偏好收集',
  fields: [
    {
      key: 'focus',
      label: '关注房间',
      type: 'radio',
      required: true,
      options: [
        { value: 'living', label: '客厅' },
        { value: 'bed', label: '主卧' },
      ],
    },
    { key: 'note', label: '补充说明', type: 'text' },
  ],
};

describe('PanelView', () => {
  it('survey 渲染标题与字段', () => {
    const { getByText, getByTestId } = render(
      <PanelView spec={surveySpec} onSubmit={jest.fn()} />,
    );

    expect(getByText('偏好收集')).toBeTruthy();
    expect(getByText('关注房间')).toBeTruthy();
    expect(getByTestId('survey-field-focus')).toBeTruthy();
  });

  it('必填未填时提交按钮禁用', () => {
    const { getByTestId } = render(<PanelView spec={surveySpec} onSubmit={jest.fn()} />);
    expect((getByTestId('panel-survey-submit') as HTMLButtonElement).disabled).toBe(true);
  });

  it('填写后提交，回传一次且只带 spec 声明的字段', async () => {
    const onSubmit = jest.fn();
    const { getByTestId, getByText } = render(
      <PanelView spec={surveySpec} onSubmit={onSubmit} />,
    );

    fireEvent.click(getByText('客厅'));
    const note = getByTestId('survey-field-note').querySelector('input');
    expect(note).toBeTruthy();
    fireEvent.change(note as HTMLInputElement, { target: { value: '加个湿度' } });

    await waitFor(() =>
      expect((getByTestId('panel-survey-submit') as HTMLButtonElement).disabled).toBe(false),
    );
    fireEvent.click(getByTestId('panel-survey-submit'));

    expect(onSubmit).toHaveBeenCalledTimes(1);
    expect(onSubmit.mock.calls[0][0]).toEqual({
      panelId: 'panel-1',
      kind: 'survey',
      values: { focus: 'living', note: '加个湿度' },
    });
  });

  it('variants / draft 未实现时显式降级', () => {
    const variants: PanelSpec = {
      v: 1,
      id: 'p2',
      kind: 'variants',
      layout: 2,
      variants: [
        { id: 'A', title: 'A', html: '<div>A</div>' },
        { id: 'B', title: 'B', html: '<div>B</div>' },
      ],
    };
    const { getByTestId, rerender } = render(
      <PanelView spec={variants} onSubmit={jest.fn()} />,
    );
    expect(getByTestId('panel-unsupported').textContent).toContain('2 个方案');

    const draft: PanelSpec = {
      v: 1,
      id: 'p3',
      kind: 'draft',
      format: 'html',
      source: '<div>草稿</div>',
    };
    rerender(<PanelView spec={draft} onSubmit={jest.fn()} />);
    expect(getByTestId('panel-unsupported').textContent).toContain('沙箱宿主');
  });
});
