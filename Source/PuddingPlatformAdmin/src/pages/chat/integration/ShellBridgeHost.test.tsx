import { act, fireEvent, render, screen } from '@testing-library/react';
import * as React from 'react';
import { SideArtifactProvider, useSideArtifact } from '../sideArtifact/SideArtifactContext';
import { ShellBridgeHost } from './ShellBridgeHost';
import { DOC_PANEL_OPENED, TOOL_PANEL_OPENED, type ShellTransport } from './shellBridge';

/**
 * 装配层面的校验：桥本身（纯逻辑）已在 shellBridge.test 覆盖，
 * 这里只验它**确实挂在文档面板生命周期上**——否则又是"组件测了、接线没测"。
 */
const makeTransport = () => {
  const posted: unknown[] = [];
  let listener: ((event: { data: unknown }) => void) | null = null;
  const transport: ShellTransport = {
    postMessage: (message) => posted.push(message),
    addMessageListener: (l) => {
      listener = l;
      return () => {
        listener = null;
      };
    },
  };
  return {
    transport,
    posted,
    // act 包裹：原生消息是**外部**事件，不包会被 React 当成非受控更新，
    // 断言在 state 提交前执行 —— 同之前 jsdom 拖拽那个坑。
    emit: (data: unknown) => act(() => listener?.({ data })),
  };
};

const Harness: React.FC<{ transport: ShellTransport }> = ({ transport }) => {
  const side = useSideArtifact();
  return (
    <div>
      <ShellBridgeHost transport={transport} />
      <button
        data-testid="open-doc"
        onClick={() => side?.openDocument({ name: 'a.md', markdown: '# a' })}
      >
        展开阅读
      </button>
      <span data-testid="open">{String(side?.open ?? 'no-provider')}</span>
    </div>
  );
};

const renderHarness = () => {
  const fake = makeTransport();
  render(
    <SideArtifactProvider>
      <Harness transport={fake.transport} />
    </SideArtifactProvider>,
  );
  return fake;
};

describe('ShellBridgeHost', () => {
  it('打开文档面板时通知原生收起工具面板', () => {
    const fake = renderHarness();

    fireEvent.click(screen.getByTestId('open-doc'));

    expect(fake.posted).toEqual([{ type: DOC_PANEL_OPENED }]);
  });

  it('原生通知工具面板展开时，文档面板被收起', () => {
    const fake = renderHarness();
    fireEvent.click(screen.getByTestId('open-doc'));
    expect(screen.getByTestId('open').textContent).toBe('true');

    fake.emit({ type: TOOL_PANEL_OPENED });

    expect(screen.getByTestId('open').textContent).toBe('false');
  });

  it('面板一直开着时不会因重渲染反复通知原生', () => {
    const fake = renderHarness();
    fireEvent.click(screen.getByTestId('open-doc'));
    // 再点一次：state 仍是 open，不该产生第二条通知
    fireEvent.click(screen.getByTestId('open-doc'));

    expect(fake.posted).toHaveLength(1);
  });
});
