// ── 外壳桥（Web ↔ 原生工具区互斥的第一半）──────────────────────
//
// 起因：三区并存时"聊天被压窄"。外部评审给的唯一推荐里，第一步是让
// **Web 内容面板与原生工具面板互斥展开**。
//
// ## 为什么不能直接写
//
// 实测（2026-10-20）：`PuddingDesktop.WpfArchive/Views/WorkbenchView.xaml.cs`
// 只打开了 `IsWebMessageEnabled = true`，**两端都没有 handler** ——
// 全仓对 `WebMessageReceived` / `ExecuteScriptAsync` / `PostWebMessageAsJson`
// 零命中。也就是说：通道开关开着，但没有任何消息在跑。
//
// 所以这一层先把 **Web 侧**写成可测的纯逻辑：有通道就走通道，
// 没通道（浏览器开发、单测、旧版外壳）就安静降级，不报错、不阻塞渲染。
//
// ## 契约（与原生约定，原生侧待实现）
//
//   Web → 原生 : { type: 'pudding.docPanel.opened' }
//                意义：我要占这块宽度了，请你收起工具面板。
//   原生 → Web : { type: 'pudding.toolPanel.opened' }
//                意义：工具面板要展开，请你收起文档面板。
//
// 两边都**只在自己打开时通知**，由对方决定收起 —— 不需要共享宽度数字，
// 这在跨 WebView 边界、拿不到对方占宽的情况下仍然可判定。
// ─────────────────────────────────────────────────────────────

export const DOC_PANEL_OPENED = 'pudding.docPanel.opened';
export const TOOL_PANEL_OPENED = 'pudding.toolPanel.opened';

export interface WebViewMessageEventLike {
  data: unknown;
}

/** 外壳通道。原生侧实现后由 `window.chrome.webview` 提供。 */
export interface ShellTransport {
  postMessage: (message: unknown) => void;
  addMessageListener: (listener: (event: WebViewMessageEventLike) => void) => () => void;
}

interface ChromeWebView {
  postMessage?: (message: unknown) => void;
  addEventListener?: (type: 'message', listener: (event: WebViewMessageEventLike) => void) => void;
  removeEventListener?: (type: 'message', listener: (event: WebViewMessageEventLike) => void) => void;
}

/**
 * 探测 WebView2 宿主。浏览器 / jsdom 下返回 null —— 调用方据此整体降级。
 * 用 `window.chrome.webview` 而不是 UA 嗅探：能力检测比字符串可靠。
 */
export function detectShellTransport(win?: Window | null): ShellTransport | null {
  const target = win ?? (typeof window === 'undefined' ? null : window);
  const webview = (target as (Window & { chrome?: { webview?: ChromeWebView } }) | null)?.chrome
    ?.webview;
  if (!webview?.postMessage || !webview.addEventListener) return null;

  return {
    postMessage: (message) => webview.postMessage?.(message),
    addMessageListener: (listener) => {
      const handler = (event: WebViewMessageEventLike) => listener(event);
      webview.addEventListener?.('message', handler);
      return () => webview.removeEventListener?.('message', handler);
    },
  };
}

/** 只认我们自己的消息：外来 message（别的脚本/扩展）一律忽略。 */
export function readShellMessage(data: unknown): { type: string } | null {
  if (typeof data !== 'object' || data === null) return null;
  const type = (data as { type?: unknown }).type;
  if (typeof type !== 'string') return null;
  if (type !== DOC_PANEL_OPENED && type !== TOOL_PANEL_OPENED) return null;
  return { type };
}

export interface ShellBridgeHandlers {
  /** 原生工具面板即将展开 ⇒ Web 收起文档面板。 */
  onToolPanelOpened: () => void;
}

export interface ShellBridge {
  /** 文档面板打开时调用。没有通道时是 no-op。 */
  notifyDocPanelOpened: () => void;
  dispose: () => void;
}

/**
 * 挂上桥。`transport` 可注入，便于单测；不传则自动探测。
 * 返回的 `dispose` 必须调用，否则热更新/切页会累积监听。
 */
export function attachShellBridge(
  handlers: ShellBridgeHandlers,
  transport: ShellTransport | null = detectShellTransport(),
): ShellBridge {
  if (!transport) {
    return { notifyDocPanelOpened: () => undefined, dispose: () => undefined };
  }

  const remove = transport.addMessageListener((event) => {
    const message = readShellMessage(event.data);
    if (!message) return;
    if (message.type === TOOL_PANEL_OPENED) handlers.onToolPanelOpened();
  });

  return {
    notifyDocPanelOpened: () => transport.postMessage({ type: DOC_PANEL_OPENED }),
    dispose: remove,
  };
}
