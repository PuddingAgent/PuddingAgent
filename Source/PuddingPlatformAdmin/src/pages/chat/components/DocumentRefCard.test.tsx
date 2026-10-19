import { fireEvent, render } from '@testing-library/react';
import * as React from 'react';
import { DocumentRefCard } from './DocumentRefCard';
import type { DocumentRef } from '../markdown/documentRefs';

/**
 * 文档引用卡：展示 + 触发，不碰文件系统。
 */
const workspaceRef: DocumentRef = {
  kind: 'workspace',
  raw: 'Docs/12_features/聊天富媒体组件设计方案-2026-10-19.md',
  target: 'Docs/12_features/聊天富媒体组件设计方案-2026-10-19.md',
  ext: 'md',
};

const artifactRef: DocumentRef = {
  kind: 'artifact',
  raw: 'artifact://vision-abc123',
  target: 'vision-abc123',
  ext: '',
};

describe('DocumentRefCard', () => {
  it('工作区文档：显示文件名与完整路径', () => {
    const { getByTestId } = render(<DocumentRefCard ref={workspaceRef} onOpen={jest.fn()} />);

    const card = getByTestId('doc-ref-workspace');
    expect(card.textContent).toContain('聊天富媒体组件设计方案-2026-10-19.md');
    expect(card.textContent).toContain('Docs/12_features/');
    expect(card.textContent).toContain('md');
  });

  it('点击把原始 ref 交回调用方', () => {
    const onOpen = jest.fn();
    const { getByTestId } = render(<DocumentRefCard ref={workspaceRef} onOpen={onOpen} />);

    fireEvent.click(getByTestId('doc-ref-workspace'));
    expect(onOpen).toHaveBeenCalledTimes(1);
    expect(onOpen.mock.calls[0][0]).toEqual(workspaceRef);
  });

  it('artifact 引用显示 artifact 徽标', () => {
    const { getByTestId } = render(<DocumentRefCard ref={artifactRef} onOpen={jest.fn()} />);
    expect(getByTestId('doc-ref-artifact').textContent).toContain('artifact');
  });

  it('没有 onOpen 时按钮禁用（不假装可点）', () => {
    const { getByTestId } = render(<DocumentRefCard ref={workspaceRef} />);
    expect((getByTestId('doc-ref-workspace') as HTMLButtonElement).disabled).toBe(true);
  });

  it('meta 透传显示（大小等）', () => {
    const { getByTestId } = render(
      <DocumentRefCard ref={workspaceRef} onOpen={jest.fn()} meta="31 KB" />,
    );
    expect(getByTestId('doc-ref-workspace').textContent).toContain('31 KB');
  });
});
