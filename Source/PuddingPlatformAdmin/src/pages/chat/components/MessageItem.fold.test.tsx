import { fireEvent, render, screen } from '@testing-library/react';
import * as React from 'react';
import MessageItem from './MessageItem';

/**
 * 折叠接入：确认折叠只发生在"落定后的长消息"，
 * 流式期间必须原样渲染（内容还在增长，折叠会跳）。
 */
jest.mock('../styles', () => {
  const styles = new Proxy(
    {},
    {
      get: (_target, prop) => String(prop),
    },
  );
  return {
    useChatStyles: () => ({ styles }),
  };
});

jest.mock('prismjs', () => ({
  highlightElement: jest.fn(),
}));

jest.mock('@/utils/debug', () => ({
  isPerfDiagnosticsEnabled: jest.fn(() => false),
  recordPerfEvent: jest.fn(),
}));

const longDoc = [
  '# 温度巡检报告',
  '## 一、结论',
  '客厅最高。'.repeat(200),
  '## 二、异常',
  '主卧偏低。',
  '## 三、建议',
  '开窗。',
].join('\n');

const shortDoc = '客厅 26.1°C，其余平稳。';

describe('MessageItem 长消息折叠接入', () => {
  it('落定的长消息折叠为摘要卡', () => {
    render(<MessageItem markdownText={longDoc} />);
    expect(screen.getByTestId('foldable-message-collapsed')).toBeTruthy();
  });

  it('短消息不折叠', () => {
    render(<MessageItem markdownText={shortDoc} />);
    expect(screen.queryByTestId('foldable-message-collapsed')).toBeNull();
  });

  it('流式期间不折叠（内容还在增长）', () => {
    render(<MessageItem markdownText={longDoc} isStreaming />);
    expect(screen.queryByTestId('foldable-message-collapsed')).toBeNull();
  });

  it('传入 onOpenMessageInSide 时按钮可点，并回传全文', () => {
    const onOpen = jest.fn();
    render(<MessageItem markdownText={longDoc} onOpenMessageInSide={onOpen} />);

    fireEvent.click(screen.getByTestId('fold-open-side'));
    expect(onOpen).toHaveBeenCalledTimes(1);
    expect(onOpen.mock.calls[0][0]).toBe(longDoc);
  });

  it('未传回调时不渲染「在右侧打开」（避免死按钮）', () => {
    render(<MessageItem markdownText={longDoc} />);
    expect(screen.queryByTestId('fold-open-side')).toBeNull();
    // 但「在对话中展开」始终可用——折叠是默认不是强制。
    expect(screen.getByTestId('fold-expand')).toBeTruthy();
  });
});
