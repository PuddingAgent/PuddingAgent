// ── UserAvatarUpload 纯逻辑测试：裁剪几何 + 文件校验 + 受控行为 ──

import { render, screen } from '@testing-library/react';
import * as React from 'react';
import {
  CROP_VIEWPORT,
  computeCropSourceRect,
  validateAvatarFile,
  default as UserAvatarUpload,
} from './UserAvatarUpload';

describe('UserAvatarUpload（受控组件）', () => {
  it('renders the avatar from the controlled avatarUrl prop', () => {
    render(
      <UserAvatarUpload userId="alice" avatarUrl="/user-avatars/a.png" />,
    );
    const img = screen
      .getAllByRole('img')
      .find((el) => (el as HTMLImageElement).src.includes('/user-avatars/'));
    expect((img as HTMLImageElement).src).toContain('/user-avatars/a.png');
  });

  it('exposes a 更换头像 button targeting the given userId', () => {
    render(<UserAvatarUpload userId="alice" />);
    expect(screen.getByRole('button', { name: /更换头像/ })).toBeTruthy();
  });
});

describe('validateAvatarFile', () => {
  it('accepts PNG / JPG / WebP within the size limit', () => {
    const png = new File(['x'], 'a.png', { type: 'image/png' });
    Object.defineProperty(png, 'size', { value: 2 * 1024 * 1024 });
    expect(validateAvatarFile(png)).toBeNull();

    const jpg = new File(['x'], 'b.jpg', { type: 'image/jpeg' });
    Object.defineProperty(jpg, 'size', { value: 1024 });
    expect(validateAvatarFile(jpg)).toBeNull();

    const webp = new File(['x'], 'c.webp', { type: 'image/webp' });
    Object.defineProperty(webp, 'size', { value: 1024 });
    expect(validateAvatarFile(webp)).toBeNull();
  });

  it('rejects non-image files', () => {
    const txt = new File(['x'], 'd.txt', { type: 'text/plain' });
    expect(validateAvatarFile(txt)).toContain('仅支持');
  });

  it('rejects files larger than the limit', () => {
    const big = new File(['x'], 'e.png', { type: 'image/png' });
    Object.defineProperty(big, 'size', { value: 6 * 1024 * 1024 });
    expect(validateAvatarFile(big, 5)).toContain('5 MB');
  });
});

describe('computeCropSourceRect', () => {
  it('returns a square source rect inside the natural bounds at zoom=1', () => {
    // 400x200 横图，cover 后高度方向占满视口
    const rect = computeCropSourceRect({
      naturalWidth: 400,
      naturalHeight: 200,
      viewport: CROP_VIEWPORT,
      zoom: 1,
      offsetX: 0,
      offsetY: 0,
    });
    expect(rect.size).toBeGreaterThan(0);
    expect(rect.x).toBeGreaterThanOrEqual(0);
    expect(rect.y).toBeGreaterThanOrEqual(0);
    expect(rect.x + rect.size).toBeLessThanOrEqual(400 + 0.01);
    expect(rect.y + rect.size).toBeLessThanOrEqual(200 + 0.01);
    // 正方形
    expect(rect.size).toBeCloseTo(280 / 1.4, 0); // baseScale = 280/200 = 1.4
  });

  it('clamps pan offsets so the image always covers the viewport', () => {
    const rect = computeCropSourceRect({
      naturalWidth: 100,
      naturalHeight: 100,
      viewport: CROP_VIEWPORT,
      zoom: 2,
      offsetX: 9999,
      offsetY: -9999,
    });
    expect(rect.x).toBeGreaterThanOrEqual(0);
    expect(rect.y).toBeGreaterThanOrEqual(0);
    expect(rect.x + rect.size).toBeLessThanOrEqual(100.01);
    expect(rect.y + rect.size).toBeLessThanOrEqual(100.01);
  });

  it('zooming shrinks the sampled source region', () => {
    const base = computeCropSourceRect({
      naturalWidth: 200,
      naturalHeight: 200,
      viewport: CROP_VIEWPORT,
      zoom: 1,
      offsetX: 0,
      offsetY: 0,
    });
    const zoomed = computeCropSourceRect({
      naturalWidth: 200,
      naturalHeight: 200,
      viewport: CROP_VIEWPORT,
      zoom: 2,
      offsetX: 0,
      offsetY: 0,
    });
    expect(zoomed.size).toBeLessThan(base.size);
  });
});
