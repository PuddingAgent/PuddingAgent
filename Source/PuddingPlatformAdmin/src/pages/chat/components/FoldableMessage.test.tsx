import { fireEvent, render } from '@testing-library/react';
import * as React from 'react';
import { FoldableMessage } from './FoldableMessage';

/**
 * 折叠卡：短消息零改动、长消息默认折叠、且永远可展开。
 * "可展开"是硬要求——折叠是默认，不是强制。
 */
const longDoc = [
  '# 温度巡检报告',
  '## 一、结论',
  '客厅最高。'.repeat(400),
  '## 二、异常',
  '主卧偏低。',
  '## 三、建议',
  '开窗。',
].join('\n');

const shortText = '客厅 26.1°C，其余平稳。';

describe('FoldableMessage', () => {
  it('短消息直接渲染 children，不出折叠 UI', () => {
    const { getByText, queryByTestId } = render(
      <FoldableMessage markdown={shortText}>
        <div>完整内容</div>
      </FoldableMessage>,
    );

    expect(getByText('完整内容')).toBeTruthy();
    expect(queryByTestId('foldable-message-collapsed')).toBeNull();
  });

  it('长消息默认折叠：显示摘要与统计，不挂载完整内容', () => {
    const { getByTestId, queryByText } = render(
      <FoldableMessage markdown={longDoc}>
        <div>完整内容</div>
      </FoldableMessage>,
    );

    expect(getByTestId('foldable-message-collapsed')).toBeTruthy();
    expect(getByTestId('fold-excerpt').textContent).toContain('温度巡检报告');
    expect(getByTestId('fold-stats').textContent).toContain('4 节');
    expect(queryByText('完整内容')).toBeNull();
  });

  it('点「在对话中展开」渲染完整内容，并可收起', () => {
    const { getByTestId, getByText, queryByTestId } = render(
      <FoldableMessage markdown={longDoc}>
        <div>完整内容</div>
      </FoldableMessage>,
    );

    fireEvent.click(getByTestId('fold-expand'));
    expect(getByText('完整内容')).toBeTruthy();

    // antd 5 的 autoInsertSpace 会把两字按钮渲染成「收 起」，文本匹配不可靠。
    fireEvent.click(getByTestId('fold-collapse'));
    expect(queryByTestId('foldable-message-collapsed')).toBeTruthy();
  });

  it('「在右侧打开」触发回调（折叠状态下也可用）', () => {
    const onOpenInSide = jest.fn();
    const { getByTestId } = render(
      <FoldableMessage markdown={longDoc} onOpenInSide={onOpenInSide}>
        <div>完整内容</div>
      </FoldableMessage>,
    );

    fireEvent.click(getByTestId('fold-open-side'));
    expect(onOpenInSide).toHaveBeenCalledTimes(1);
  });

  it('未提供 onOpenInSide 时不渲染该按钮（避免死按钮）', () => {
    const { queryByTestId } = render(
      <FoldableMessage markdown={longDoc}>
        <div>完整内容</div>
      </FoldableMessage>,
    );
    expect(queryByTestId('fold-open-side')).toBeNull();
  });
});
