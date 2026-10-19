import { act, fireEvent, render, screen } from '@testing-library/react';
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

describe('SidePanelResizer 拖拽', () => {
  /**
   * jsdom 没有 `PointerEvent` 构造器，testing-library 会回退成普通 `Event`，
   * 带不上 `clientX`（实测：delta 变 NaN，宽度永远不变）。
   * 所以用 `MouseEvent` 派发同名的 pointer 事件 —— 监听方只读 clientX。
   *
   * 另外必须包在 `act()` 里：直接 dispatch 不会被 React 自动批处理包装，
   * 断言会在 state 更新前执行，看到的是旧宽度（同样表现为"拖不动"）。
   */
  const firePointer = (target: EventTarget, type: string, clientX: number) => {
    act(() => {
      target.dispatchEvent(new MouseEvent(type, { clientX, bubbles: true }));
    });
  };

  const DraggableHarness: React.FC<{ commits: number[] }> = ({ commits }) => {
    const [w, setW] = React.useState(420);
    return (
      <>
        <SidePanelResizer
          width={w}
          onWidthChange={setW}
          onCommit={(c) => commits.push(c)}
        />
        <span data-testid="live-width">{w}</span>
      </>
    );
  };

  it('拖动时实时变宽，松手只提交一次且提交的是**最终**宽度', () => {
    const commits: number[] = [];
    render(<DraggableHarness commits={commits} />);
    const handle = screen.getByTestId('side-panel-resizer');

    firePointer(handle, 'pointerdown', 800);
    firePointer(window, 'pointermove', 740);

    // 面板在右侧：向左拖 60px ⇒ 420 + 60
    expect(screen.getByTestId('live-width').textContent).toBe('480');
    // 拖动过程不落盘，避免逐帧写 storage
    expect(commits).toEqual([]);

    firePointer(window, 'pointerup', 740);

    // 回归：曾经提交的是拖动**开始**时的宽度（420），拖动结果被丢掉。
    expect(commits).toEqual([480]);
  });

  it('拖拽期间禁止选中文本，结束后恢复', () => {
    render(<DraggableHarness commits={[]} />);
    const handle = screen.getByTestId('side-panel-resizer');

    firePointer(handle, 'pointerdown', 800);
    expect(document.body.style.userSelect).toBe('none');

    firePointer(window, 'pointerup', 800);
    // 不恢复的话整个页面都选不中文字，比不能拖更坑。
    expect(document.body.style.userSelect).toBe('');
    expect(document.body.style.cursor).toBe('');
  });
});
