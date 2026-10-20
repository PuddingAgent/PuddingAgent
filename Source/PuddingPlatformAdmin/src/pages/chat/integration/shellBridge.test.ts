import {
  attachShellBridge,
  detectShellTransport,
  DOC_PANEL_OPENED,
  readShellMessage,
  TOOL_PANEL_OPENED,
  type ShellTransport,
  type WebViewMessageEventLike,
} from './shellBridge';

/**
 * 外壳桥的纯逻辑测试。
 *
 * 重点不是"消息能发出去"这种 trivial 事实，而是三条**失效路径**：
 * 没有通道时要安静降级、外来消息要忽略、dispose 之后不能再回调。
 * 这三条任何一条破了，表现都是"面板莫名其妙被关掉"或"内存里挂着旧监听"。
 */
const makeTransport = () => {
  const posted: unknown[] = [];
  let listener: ((event: WebViewMessageEventLike) => void) | null = null;
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
    emit: (data: unknown) => listener?.({ data }),
    hasListener: () => listener !== null,
  };
};

describe('shellBridge', () => {
  it('文档面板打开时通知原生收起工具面板', () => {
    const fake = makeTransport();
    const bridge = attachShellBridge({ onToolPanelOpened: jest.fn() }, fake.transport);

    bridge.notifyDocPanelOpened();

    expect(fake.posted).toEqual([{ type: DOC_PANEL_OPENED }]);
  });

  it('收到原生「工具面板展开」时回调宿主，其它消息一律忽略', () => {
    const fake = makeTransport();
    const onToolPanelOpened = jest.fn();
    attachShellBridge({ onToolPanelOpened }, fake.transport);

    fake.emit({ type: TOOL_PANEL_OPENED });
    expect(onToolPanelOpened).toHaveBeenCalledTimes(1);

    // 外来消息：别的脚本、别的扩展、格式不对的
    fake.emit({ type: 'some.other.message' });
    fake.emit({ type: 123 });
    fake.emit('pudding.toolPanel.opened');
    fake.emit(null);
    expect(onToolPanelOpened).toHaveBeenCalledTimes(1);
  });

  it('dispose 之后不再回调（防热更新累积监听）', () => {
    const fake = makeTransport();
    const onToolPanelOpened = jest.fn();
    const bridge = attachShellBridge({ onToolPanelOpened }, fake.transport);

    bridge.dispose();

    expect(fake.hasListener()).toBe(false);
    fake.emit({ type: TOOL_PANEL_OPENED });
    expect(onToolPanelOpened).not.toHaveBeenCalled();
  });

  it('没有通道时整体降级：不抛错，通知是 no-op', () => {
    const bridge = attachShellBridge({ onToolPanelOpened: jest.fn() }, null);

    expect(() => bridge.notifyDocPanelOpened()).not.toThrow();
    expect(() => bridge.dispose()).not.toThrow();
  });

  it('浏览器环境探测不到 WebView2 时返回 null', () => {
    // jsdom 没有 window.chrome.webview —— 正好是"浏览器开发"的场景
    expect(detectShellTransport()).toBeNull();
  });

  it('只接受契约内的两种 type', () => {
    expect(readShellMessage({ type: DOC_PANEL_OPENED })).toEqual({ type: DOC_PANEL_OPENED });
    expect(readShellMessage({ type: TOOL_PANEL_OPENED })).toEqual({ type: TOOL_PANEL_OPENED });
    expect(readShellMessage({ type: 'other' })).toBeNull();
    expect(readShellMessage(undefined)).toBeNull();
    expect(readShellMessage([])).toBeNull();
  });
});
