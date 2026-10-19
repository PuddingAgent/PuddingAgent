import { fireEvent, render, screen } from '@testing-library/react';
import * as React from 'react';
import { SideArtifactPanel } from './SideArtifactPanel';

/**
 * 右侧产物区外壳：只负责 tab / 宽度 / 关闭，不关心内容是什么。
 * 重点验证受控与非受控两种用法——"在右侧打开全文"需要外部程序化切 tab。
 */
const tabs = [
  { key: 'panel', label: '面板', content: <div>面板内容</div> },
  { key: 'doc', label: '文档', content: <div>文档内容</div> },
];

describe('SideArtifactPanel', () => {
  it('open=false 时不渲染', () => {
    const { container } = render(<SideArtifactPanel open={false} tabs={tabs} />);
    expect(container.firstChild).toBeNull();
  });

  it('默认显示第一个 tab', () => {
    render(<SideArtifactPanel open tabs={tabs} />);
    expect(screen.getByText('面板内容')).toBeTruthy();
    expect(screen.queryByText('文档内容')).toBeNull();
  });

  it('非受控：点 tab 自行切换并通知外部', () => {
    const onTabChange = jest.fn();
    render(<SideArtifactPanel open tabs={tabs} onTabChange={onTabChange} />);

    fireEvent.click(screen.getByText('文档'));
    expect(screen.getByText('文档内容')).toBeTruthy();
    expect(onTabChange).toHaveBeenCalledWith('doc');
  });

  it('受控：activeKey 变化由外部驱动', () => {
    const { rerender } = render(<SideArtifactPanel open tabs={tabs} activeKey="panel" />);
    expect(screen.getByText('面板内容')).toBeTruthy();

    rerender(<SideArtifactPanel open tabs={tabs} activeKey="doc" />);
    expect(screen.getByText('文档内容')).toBeTruthy();
  });

  it('关闭按钮触发回调', () => {
    const onClose = jest.fn();
    render(<SideArtifactPanel open tabs={tabs} onClose={onClose} />);

    fireEvent.click(screen.getByTestId('side-artifact-close'));
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('未传 onClose 时不渲染关闭按钮（避免死按钮）', () => {
    render(<SideArtifactPanel open tabs={tabs} />);
    expect(screen.queryByTestId('side-artifact-close')).toBeNull();
  });

  it('空 tabs 显示占位，不崩', () => {
    render(<SideArtifactPanel open tabs={[]} />);
    expect(screen.getByText('还没有内容')).toBeTruthy();
  });
});
