import { fireEvent, render, screen } from '@testing-library/react';
import * as React from 'react';
import { SideArtifactProvider, useSideArtifact } from './SideArtifactContext';
import { SideDocumentPanel } from '../components/SideDocumentPanel';

/**
 * 右侧产物状态：这是跨 4 层组件的 UI 动作，用 context 而不是 prop drilling。
 * 重点验证三件事：openDocument 生效、close 只收起不清内容、无 Provider 时安全降级。
 */
const Probe: React.FC = () => {
  const side = useSideArtifact();
  return (
    <div>
      <span data-testid="open">{String(side?.open ?? 'no-provider')}</span>
      <span data-testid="name">{side?.document?.name ?? '-'}</span>
      <button
        data-testid="do-open"
        onClick={() => side?.openDocument({ name: 'a.md', markdown: '# a' })}
      >
        open
      </button>
      <button data-testid="do-close" onClick={() => side?.close()}>
        close
      </button>
    </div>
  );
};

describe('SideArtifactContext', () => {
  it('无 Provider 时 hook 返回 null（调用方必须显式降级）', () => {
    render(<Probe />);
    expect(screen.getByTestId('open').textContent).toBe('no-provider');
    expect(screen.getByTestId('name').textContent).toBe('-');
  });

  it('openDocument 打开面板并设置文档', () => {
    render(
      <SideArtifactProvider>
        <Probe />
      </SideArtifactProvider>,
    );

    expect(screen.getByTestId('open').textContent).toBe('false');
    fireEvent.click(screen.getByTestId('do-open'));

    expect(screen.getByTestId('open').textContent).toBe('true');
    expect(screen.getByTestId('name').textContent).toBe('a.md');
  });

  it('close 只收起，不清空文档（再次打开还在）', () => {
    render(
      <SideArtifactProvider>
        <Probe />
      </SideArtifactProvider>,
    );

    fireEvent.click(screen.getByTestId('do-open'));
    fireEvent.click(screen.getByTestId('do-close'));

    expect(screen.getByTestId('open').textContent).toBe('false');
    expect(screen.getByTestId('name').textContent).toBe('a.md');
  });
});

describe('SideDocumentPanel', () => {
  it('没有文档时不渲染（不占布局）', () => {
    const { container } = render(
      <SideArtifactProvider>
        <SideDocumentPanel />
      </SideArtifactProvider>,
    );
    expect(container.firstChild).toBeNull();
  });

  it('有文档时渲染面板与文档名', () => {
    const Opener: React.FC = () => {
      const side = useSideArtifact();
      // 只能用 ref 做一次性守卫：`side` 在每次 state 变化后都是新对象
      // （useMemo 依赖 state），拿它做 effect 依赖会「openDocument → state 变
      // → side 变 → 再 openDocument」无限循环——实测直接把 jest 挂死。
      const opened = React.useRef(false);
      React.useEffect(() => {
        if (opened.current || !side) return;
        opened.current = true;
        side.openDocument({ name: '报告.md', markdown: '# 一\n正文' });
      }, [side]);
      return null;
    };

    render(
      <SideArtifactProvider>
        <Opener />
        <SideDocumentPanel />
      </SideArtifactProvider>,
    );

    expect(screen.getByTestId('side-artifact-panel')).toBeTruthy();
    expect(screen.getByTestId('document-preview-name').textContent).toBe('报告.md');
  });
});
