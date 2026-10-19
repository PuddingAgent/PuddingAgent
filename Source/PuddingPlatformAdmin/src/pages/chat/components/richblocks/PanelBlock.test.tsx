import { fireEvent, render, screen } from '@testing-library/react';
import * as React from 'react';
import { PanelBlock } from './PanelBlock';
import { UiSubmissionProvider } from '../../uiSubmission/UiSubmissionContext';
import type { UiSubmissionPayload } from '../../panel/submission';

/**
 * ```panel 围栏渲染器。
 *
 * 重点验证三件事：没通道时**禁用提交并说明原因**、有通道时提交内容能带回、
 * spec 坏掉时**显式报错而不是渲染半成品**。
 */
const surveySpec = (overrides: Record<string, unknown> = {}) =>
  JSON.stringify({
    v: 1,
    id: 'p1',
    kind: 'survey',
    title: '需求对齐',
    fields: [{ key: 'goal', label: '目标', type: 'text' }],
    ...overrides,
  });

describe('PanelBlock', () => {
  it('没有回传通道时：面板可见、提交禁用、并说明原因', () => {
    render(<PanelBlock code={surveySpec()} />);

    expect(screen.getByTestId('panel-block-no-channel')).toBeTruthy();
    // 面板本身仍渲染（先让人看清要填什么）
    expect(screen.getByTestId('panel-survey')).toBeTruthy();
    const submit = screen.getByTestId('panel-survey-submit') as HTMLButtonElement;
    expect(submit.disabled).toBe(true);
    // 不给点了没反应的按钮
    expect(screen.getByRole('note').textContent).toContain('没有回传通道');
  });

  it('有回传通道时：提交把 payload 交回宿主', () => {
    const received: UiSubmissionPayload[] = [];
    render(
      <UiSubmissionProvider onSubmit={(p) => received.push(p)}>
        <PanelBlock code={surveySpec()} />
      </UiSubmissionProvider>,
    );

    expect(screen.getByTestId('panel-block')).toBeTruthy();
    fireEvent.click(screen.getByTestId('panel-survey-submit'));

    expect(received).toHaveLength(1);
    expect(received[0].panelId).toBe('p1');
    expect(received[0].kind).toBe('survey');
  });

  it('JSON 坏掉时显式报错，不渲染半成品面板', () => {
    render(<PanelBlock code={'{ not json'} />);

    const error = screen.getByTestId('panel-block-error');
    expect(error.textContent).toContain('JSON 解析失败');
    expect(screen.queryByTestId('panel-survey')).toBeNull();
  });

  it('spec 不合约时列出前几条问题（带路径）', () => {
    render(<PanelBlock code={JSON.stringify({ v: 1, id: 'p1', kind: 'survey' })} />);

    const error = screen.getByTestId('panel-block-error');
    expect(error.textContent).toContain('$.fields');
    expect(screen.queryByTestId('panel-survey')).toBeNull();
  });
});
