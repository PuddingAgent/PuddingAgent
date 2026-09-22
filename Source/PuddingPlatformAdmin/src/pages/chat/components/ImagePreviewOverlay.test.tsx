import { fireEvent, render, screen } from '@testing-library/react';
import React from 'react';
import { ImagePreviewOverlay, ZoomableImage } from './ImagePreviewOverlay';

const ITEMS = [
  { src: '/api/workspaces/default/vision-artifacts/vision-a', alt: '图 1/3' },
  { src: '/api/workspaces/default/vision-artifacts/vision-b', alt: '图 2/3' },
  { src: '/api/workspaces/default/vision-artifacts/vision-c', alt: '图 3/3' },
];

const Harness: React.FC<{ itemCount?: number }> = ({ itemCount = 3 }) => {
  const items = ITEMS.slice(0, itemCount);
  const [index, setIndex] = React.useState(0);
  const [open, setOpen] = React.useState(true);
  const onClose = React.useCallback(() => setOpen(false), []);
  if (!open) return <span data-testid="closed" />;
  return (
    <ImagePreviewOverlay
      items={items}
      index={index}
      onIndexChange={setIndex}
      onClose={onClose}
    />
  );
};

describe('ImagePreviewOverlay', () => {
  it('renders nothing when there is no image to preview', () => {
    render(<ImagePreviewOverlay items={[]} index={0} onClose={() => {}} />);

    expect(screen.queryByTestId('image-preview-overlay')).toBeNull();
  });

  it('renders the current image as an accessible dialog and locks body scroll', () => {
    const { unmount } = render(<Harness />);

    const overlay = screen.getByTestId('image-preview-overlay');
    expect(overlay.getAttribute('role')).toBe('dialog');
    expect(overlay.getAttribute('aria-modal')).toBe('true');
    expect(screen.getByTestId('image-preview-image').getAttribute('src')).toBe(
      ITEMS[0].src,
    );
    expect(document.body.style.overflow).toBe('hidden');

    unmount();
    expect(document.body.style.overflow).toBe('');
  });

  it('closes on Escape', () => {
    render(<Harness />);

    fireEvent.keyDown(document, { key: 'Escape' });

    expect(screen.queryByTestId('image-preview-overlay')).toBeNull();
    expect(screen.getByTestId('closed')).toBeTruthy();
  });

  it('closes on backdrop click but not on image click', () => {
    render(<Harness />);

    fireEvent.click(screen.getByTestId('image-preview-image'));
    expect(screen.queryByTestId('image-preview-overlay')).toBeTruthy();

    fireEvent.click(screen.getByTestId('image-preview-overlay'));
    expect(screen.queryByTestId('image-preview-overlay')).toBeNull();
  });

  it('closes via the close button without bubbling to the backdrop handler', () => {
    render(<Harness />);

    fireEvent.click(screen.getByTestId('image-preview-close'));

    expect(screen.queryByTestId('image-preview-overlay')).toBeNull();
  });

  it('navigates a multi-image set with buttons and wraps around', () => {
    render(<Harness />);

    expect(screen.getByTestId('image-preview-counter').textContent).toContain(
      '1 / 3',
    );

    fireEvent.click(screen.getByTestId('image-preview-next'));
    expect(screen.getByTestId('image-preview-image').getAttribute('src')).toBe(
      ITEMS[1].src,
    );
    expect(screen.getByTestId('image-preview-counter').textContent).toContain(
      '2 / 3',
    );

    // 末尾再下一张回到首张（循环），不越界成空白。
    fireEvent.click(screen.getByTestId('image-preview-next'));
    fireEvent.click(screen.getByTestId('image-preview-next'));
    expect(screen.getByTestId('image-preview-image').getAttribute('src')).toBe(
      ITEMS[0].src,
    );

    // 首张再上一张回到末张。
    fireEvent.click(screen.getByTestId('image-preview-prev'));
    expect(screen.getByTestId('image-preview-image').getAttribute('src')).toBe(
      ITEMS[2].src,
    );
  });

  it('navigates a multi-image set with arrow keys', () => {
    render(<Harness />);

    fireEvent.keyDown(document, { key: 'ArrowRight' });
    expect(screen.getByTestId('image-preview-image').getAttribute('src')).toBe(
      ITEMS[1].src,
    );

    fireEvent.keyDown(document, { key: 'ArrowLeft' });
    expect(screen.getByTestId('image-preview-image').getAttribute('src')).toBe(
      ITEMS[0].src,
    );
  });

  it('hides navigation affordances for a single image', () => {
    render(<Harness itemCount={1} />);

    expect(screen.queryByTestId('image-preview-prev')).toBeNull();
    expect(screen.queryByTestId('image-preview-next')).toBeNull();
    expect(screen.queryByTestId('image-preview-counter')).toBeNull();
  });

  it('falls back to the first image when the index is out of range', () => {
    render(
      <ImagePreviewOverlay items={ITEMS} index={9} onClose={() => {}} />,
    );

    expect(screen.getByTestId('image-preview-image').getAttribute('src')).toBe(
      ITEMS[0].src,
    );
  });
});

describe('ZoomableImage', () => {
  it('opens the same image in the overlay on click and closes on Escape', () => {
    render(
      <ZoomableImage
        src="/api/workspaces/default/vision-artifacts/vision-art"
        alt="Agent 生成的图片"
        wrapperClassName="artifactImageWrap"
        imgClassName="artifactImage"
        loading="lazy"
      />,
    );

    expect(screen.queryByTestId('image-preview-overlay')).toBeNull();

    fireEvent.click(screen.getByTestId('zoomable-image'));

    expect(screen.getByTestId('image-preview-overlay')).toBeTruthy();
    expect(screen.getByTestId('image-preview-image').getAttribute('src')).toBe(
      '/api/workspaces/default/vision-artifacts/vision-art',
    );
    // 单图预览不出现多图导航。
    expect(screen.queryByTestId('image-preview-next')).toBeNull();

    fireEvent.keyDown(document, { key: 'Escape' });
    expect(screen.queryByTestId('image-preview-overlay')).toBeNull();
  });

  it('opens on Enter and Space (keyboard equivalent of the click)', () => {
    render(<ZoomableImage src="/a.png" alt="图" />);

    const trigger = screen.getByTestId('zoomable-image');
    expect(trigger.getAttribute('role')).toBe('button');
    expect(trigger.getAttribute('tabindex')).toBe('0');

    fireEvent.keyDown(trigger, { key: 'Enter' });
    expect(screen.queryByTestId('image-preview-overlay')).toBeTruthy();

    fireEvent.keyDown(document, { key: 'Escape' });
    expect(screen.queryByTestId('image-preview-overlay')).toBeNull();

    fireEvent.keyDown(trigger, { key: ' ' });
    expect(screen.queryByTestId('image-preview-overlay')).toBeTruthy();
  });

  it('keeps the caller classes so existing image styling is preserved', () => {
    render(
      <ZoomableImage
        src="/a.png"
        alt="图"
        wrapperClassName="artifactImageWrap"
        imgClassName="artifactImage"
      />,
    );

    const trigger = screen.getByTestId('zoomable-image');
    expect(trigger.className).toContain('artifactImageWrap');
    expect(screen.getByAltText('图').className).toContain('artifactImage');
    // 包裹层除调用方 class 外，还必须带上预览样式域的 class（zoom-in 光标 / 焦点环）。
    // 注意：antd-style 的 createStyles 会哈希类名（实测为 acss-xxxx），
    // 字面量 'zoomable' 不会出现在 DOM 中，所以只能断言「多了一个类」。
    const wrapperClasses = trigger.className.trim().split(/\s+/);
    expect(wrapperClasses).toContain('artifactImageWrap');
    expect(wrapperClasses.length).toBeGreaterThan(1);
  });
});
