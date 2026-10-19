import { fireEvent, render, screen } from '@testing-library/react';
import * as React from 'react';
import {
  SideArtifactProvider,
  useSideArtifact,
  useSideArtifactActions,
  type SideDocument,
} from './SideArtifactContext';
import { SideDocumentPanel } from '../components/SideDocumentPanel';

/**
 * 右侧产物状态。
 *
 * 重点验证三件事：openDocument 生效、close 只收起不清内容、无 Provider 时安全降级。
 * 外加一条性能约束：**只消费 actions 的组件不能因文档变化而重渲染**。
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

describe('actions / state 拆分（避免跨层重渲染）', () => {
  it('只消费 actions 的组件在文档变化时不重渲染', () => {
    let renders = 0;

    const ActionOnly: React.FC = () => {
      const actions = useSideArtifactActions();
      renders += 1;
      return (
        <button
          data-testid="action-open"
          onClick={() => actions?.openDocument({ name: 'z.md', markdown: '# z' })}
        >
          open
        </button>
      );
    };

    render(
      <SideArtifactProvider>
        <ActionOnly />
      </SideArtifactProvider>,
    );

    const before = renders;
    fireEvent.click(screen.getByTestId('action-open'));

    // state 变了，但只用 actions 的组件不应该重渲染——
    // 这正是"长对话里每条长消息都白跑一遍"的根源。
    expect(renders).toBe(before);
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

  // 回归：长消息曾被伪造成「标题 + .md」并配「复制路径」，
  // 复制出来的是磁盘上不存在的路径——正好是这功能要解决的痛点反面。
  it('来源为对话消息（无 sourcePath）时不提供「复制路径」', () => {
    renderWithDocument({ name: '部署手册已写好', markdown: '# 一\n正文' });

    expect(screen.queryByTestId('document-copy-path')).toBeNull();
    // 诚实说明它来自对话，而不是一个文件。
    expect(screen.getByTestId('document-preview-name').parentElement?.textContent).toContain(
      '来自对话消息',
    );
  });

  it('有 sourcePath 时才提供「复制路径」', () => {
    renderWithDocument({
      name: '部署操作手册.md',
      markdown: '# 一\n正文',
      sourcePath: 'Docs/14_reports/2026-10-20-部署操作手册.md',
    });

    expect(screen.getByTestId('document-copy-path')).toBeTruthy();
  });
});

/** 在 Provider 里打开一份指定文档，并渲染右侧面板。 */
function renderWithDocument(doc: SideDocument) {
  const Opener: React.FC = () => {
    const side = useSideArtifact();
    // 同上面那条：只能用 ref 守卫，`side` 不能当 effect 依赖。
    const opened = React.useRef(false);
    React.useEffect(() => {
      if (opened.current || !side) return;
      opened.current = true;
      side.openDocument(doc);
    }, [side]);
    return null;
  };

  return render(
    <SideArtifactProvider>
      <Opener />
      <SideDocumentPanel />
    </SideArtifactProvider>,
  );
}
