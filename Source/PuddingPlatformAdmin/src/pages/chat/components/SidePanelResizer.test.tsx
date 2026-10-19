import { fireEvent, render, screen } from '@testing-library/react';
import * as React from 'react';
import { SidePanelResizer } from './SidePanelResizer';

/**
 * 分隔条的行为测试。
 *
 * 拖拽本身（pointerdown → pointermove → pointerup）在 jsdom 里没有真实布局，
 * 所以「拖多远变多宽」的换算放在 `sidePanelSizing.test.ts` 的纯函数里测；
 * 这里只测**键盘与复位**这两条不依赖布局的路径。
 */
describe('SidePanelResizer', () => {
  const setup = (width = 420) => {
    const changes: number[] = [];
    const commits: number[] = [];
    render(
      <SidePanelResizer
        width={width}
        onWidthChange={(w) => changes.push(w)}
        onCommit={(w) => commits.push(w)}
      />,
    );
    return { changes, commits, handle: screen.getByTestId('side-panel-resizer') };
  };

  it('暴露 separator 语义与取值区间（无障碍）', () => {
    const { handle } = setup(420);
    expect(handle.getAttribute('role')).toBe('separator');
    expect(handle.getAttribute('aria-valuenow')).toBe('420');
    expect(handle.getAttribute('aria-valuemin')).toBe('320');
    expect(handle.getAttribute('aria-valuemax')).toBe('900');
    expect(handle.getAttribute('tabindex')).toBe('0');
  });

  // 面板在右侧：左箭头 = 变宽。方向写反了会非常直观地"越按越窄"。
  it('左箭头变宽 16px 并提交', () => {
    const { handle, changes, commits } = setup(420);
    fireEvent.keyDown(handle, { key: 'ArrowLeft' });
    expect(changes).toEqual([436]);
    expect(commits).toEqual([436]);
  });

  it('右箭头变窄 16px', () => {
    const { handle, changes } = setup(420);
    fireEvent.keyDown(handle, { key: 'ArrowRight' });
    expect(changes).toEqual([404]);
  });

  it('到达上限后继续按左箭头不会越界', () => {
    const { handle, changes } = setup(900);
    fireEvent.keyDown(handle, { key: 'ArrowLeft' });
    expect(changes).toEqual([900]);
  });

  it('双击复位到默认宽度并提交', () => {
    const { handle, changes, commits } = setup(780);
    fireEvent.doubleClick(handle);
    expect(changes).toEqual([420]);
    expect(commits).toEqual([420]);
  });

  it('其它按键不改变宽度（避免误触）', () => {
    const { handle, changes } = setup(420);
    fireEvent.keyDown(handle, { key: 'Enter' });
    fireEvent.keyDown(handle, { key: 'a' });
    expect(changes).toEqual([]);
  });
});
