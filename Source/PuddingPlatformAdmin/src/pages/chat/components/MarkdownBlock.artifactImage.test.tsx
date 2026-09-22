import { fireEvent, render, screen } from '@testing-library/react';
import React from 'react';
import MarkdownBlock from './MarkdownBlock';

// 与聊天叶子组件的测试环境一致：样式键即类名（见 messageStyleContext 的 testStyles）。
const styles = new Proxy<Record<string, string>>(
  {},
  {
    get: (_target, property) => String(property),
  },
);

const WORKSPACE_ID = 'default';
const ARTIFACT_ID = `vision-${'a'.repeat(32)}`;
const ARTIFACT_SRC = `/api/workspaces/${WORKSPACE_ID}/vision-artifacts/${ARTIFACT_ID}`;

const renderBlock = (markdownText: string) =>
  render(
    <MarkdownBlock
      markdownText={markdownText}
      styles={styles}
      workspaceId={WORKSPACE_ID}
    />,
  );

describe('MarkdownBlock 制品图（language-image）', () => {
  it('renders the artifact image from the vision artifact id', () => {
    renderBlock(['```image', ARTIFACT_ID, '```'].join('\n'));

    const image = screen.getByAltText('Agent 生成的图片') as HTMLImageElement;
    expect(image.getAttribute('src')).toBe(ARTIFACT_SRC);
    expect(image.getAttribute('loading')).toBe('lazy');
  });

  it('opens the full-screen preview when clicking the artifact image', () => {
    renderBlock(['```image', ARTIFACT_ID, '```'].join('\n'));

    // 没有可点击的包裹层时，这条断言会失败 —— 反向锁住「制品图必须可放大」。
    expect(screen.queryByTestId('image-preview-overlay')).toBeNull();

    fireEvent.click(screen.getByTestId('zoomable-image'));

    expect(screen.getByTestId('image-preview-overlay')).toBeTruthy();
    expect(
      (screen.getByTestId('image-preview-image') as HTMLImageElement).getAttribute(
        'src',
      ),
    ).toBe(ARTIFACT_SRC);

    fireEvent.keyDown(document, { key: 'Escape' });
    expect(screen.queryByTestId('image-preview-overlay')).toBeNull();
  });

  it('keeps the caller wrapper class so existing artifact styling is preserved', () => {
    renderBlock(['```image', ARTIFACT_ID, '```'].join('\n'));

    const trigger = screen.getByTestId('zoomable-image');
    expect(trigger.className).toContain('artifactImageWrap');
    expect((screen.getByAltText('Agent 生成的图片') as HTMLImageElement).className).toContain(
      'artifactImage',
    );
  });

  it('does not render an image when the vision artifact id is malformed', () => {
    renderBlock(['```image', 'not-a-vision-id', '```'].join('\n'));

    expect(screen.queryByAltText('Agent 生成的图片')).toBeNull();
    expect(screen.queryByTestId('zoomable-image')).toBeNull();
  });
});
