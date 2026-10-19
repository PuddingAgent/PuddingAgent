import { fireEvent, render, screen } from '@testing-library/react';
import * as React from 'react';
import { DocumentPreview } from './DocumentPreview';

/**
 * 文档预览：文件名、大纲、空态、按钮可用性。
 * 大纲点击走"按序取 h1,h2,h3 节点"的路径，jsdom 没有 scrollIntoView，需打桩。
 */
const doc = ['# 一、结论', '正文', '## 二、异常', '正文', '### 细目'].join('\n');

beforeAll(() => {
  // jsdom 未实现；这里只关心"调到了正确节点"。
  (Element.prototype as unknown as { scrollIntoView: () => void }).scrollIntoView =
    jest.fn();
});

describe('DocumentPreview', () => {
  it('渲染文件名与大纲条目', () => {
    render(<DocumentPreview name="report.md" markdown={doc} meta="6.8 KB" />);

    expect(screen.getByTestId('document-preview-name').textContent).toBe('report.md');
    expect(screen.getByText('6.8 KB')).toBeTruthy();

    expect(screen.getByTestId('outline-item-0').textContent).toBe('一、结论');
    expect(screen.getByTestId('outline-item-2').textContent).toBe('细目');
  });

  it('点大纲调用 scrollIntoView', () => {
    const spy = Element.prototype.scrollIntoView as jest.Mock;
    spy.mockClear();

    render(<DocumentPreview name="r.md" markdown={doc} />);
    fireEvent.click(screen.getByTestId('outline-item-1'));

    expect(spy).toHaveBeenCalledTimes(1);
  });

  it('空文档显示占位且大纲为空', () => {
    render(<DocumentPreview name="empty.md" markdown="   " />);
    expect(screen.getByText('（空文档）')).toBeTruthy();
    expect(screen.getByText('无标题')).toBeTruthy();
  });

  it('未提供回调时不渲染操作按钮（避免死按钮）', () => {
    render(<DocumentPreview name="r.md" markdown={doc} />);
    expect(screen.queryByTestId('document-save-artifact')).toBeNull();
    expect(screen.queryByTestId('document-copy-path')).toBeNull();
  });

  it('提供回调时按钮可点并触发', () => {
    const onSave = jest.fn();
    const onCopy = jest.fn();

    render(
      <DocumentPreview
        name="r.md"
        markdown={doc}
        onSaveArtifact={onSave}
        onCopyPath={onCopy}
      />,
    );

    fireEvent.click(screen.getByTestId('document-save-artifact'));
    fireEvent.click(screen.getByTestId('document-copy-path'));

    expect(onSave).toHaveBeenCalledTimes(1);
    expect(onCopy).toHaveBeenCalledTimes(1);
  });

  it('markdown 里的脚本不会执行（MarkdownPreview 不启用 rehype-raw）', () => {
    const malicious = '# 标题\n\n<script>window.__x = 1</script>';
    render(<DocumentPreview name="x.md" markdown={malicious} />);

    // 作为文本出现，而不是被执行/作为脚本节点插入。
    expect(document.querySelector('script')).toBeNull();
  });
});
