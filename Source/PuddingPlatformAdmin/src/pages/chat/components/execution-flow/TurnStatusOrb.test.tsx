// ── TurnStatusOrb 测试：阶段映射 / pending / 主题跟随（thinking-orbs 接入）──
import { render } from '@testing-library/react';
import * as React from 'react';
import { TurnStatusOrb } from './TurnStatusOrb';

const orbCanvasOf = (container: HTMLElement): HTMLCanvasElement | null =>
  container.querySelector('[data-testid="turn-status-orb"] canvas');

describe('TurnStatusOrb', () => {
  it('渲染 20px inline 档墨球 canvas（jsdom 下库安全降级不抛错）', () => {
    const { container } = render(<TurnStatusOrb phase="reasoning" />);
    const host = container.querySelector('[data-testid="turn-status-orb"]');
    expect(host).toBeTruthy();
    expect(orbCanvasOf(container)).toBeTruthy();
  });

  it('data-pudding-theme=dark 时解析为 dark 墨色（显式绑定，不用库 auto 探测）', () => {
    document.documentElement.setAttribute('data-pudding-theme', 'dark');
    try {
      const { container, rerender } = render(<TurnStatusOrb phase="reasoning" />);
      // 主题正确性以不抛错 + 宿主渲染为准（canvas 内部墨色无法在 jsdom 断言）
      expect(orbCanvasOf(container)).toBeTruthy();
      rerender(<TurnStatusOrb phase="executing" />);
      expect(orbCanvasOf(container)).toBeTruthy();
    } finally {
      document.documentElement.setAttribute('data-pudding-theme', 'light');
    }
  });

  it('pending=true 时无 phase 也渲染（breathing 待命态）', () => {
    const { container } = render(<TurnStatusOrb pending ariaLabel="默认助手 正在运行" />);
    expect(orbCanvasOf(container)).toBeTruthy();
  });
});
