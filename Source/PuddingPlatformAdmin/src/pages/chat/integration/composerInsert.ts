// ── 原生 → 输入框的最后一公里 ────────────────────────────────────
//
// 场景：工具区画布的「添加到对话」按钮。画布在另一个 WebView2 里、按钮由原生
// XAML 画 —— 发起方完全不知道 React 树长什么样；而消费方是最内层的输入框叶子。
//
// 为什么单独一个文件而不是塞进 ShellBridgeHost：
//   Provider/组件里定义频道常量，会让「只想订阅」的一层被迫 import 整个组件
//   （连带 sideArtifact 依赖）。这里只留频道名与发/订两个函数，不依赖 React，
//   于是可以在没有外壳的环境里对信道本身做单测。
//
// 为什么用 DOM 事件而不是 prop 穿透：ChatPage → ChatMain → IntentConsole 三层
// 签名都要为一个低频外部指令各加一个它们永不使用的参数。这本来就是
// 「外部通道 → 应用内部」的边界转换，广播给最靠近使用方的那一层更小侵入。
// ─────────────────────────────────────────────────────────────

export const COMPOSER_INSERT_EVENT = 'pudding:composer-insert';

export interface ComposerInsertDetail {
  text: string;
}

/** 把文本广播给输入框。空文本直接丢弃——插一个空串只会让用户莫名多出空行。 */
export function publishComposerInsert(text: string): void {
  if (text.length === 0) return;
  window.dispatchEvent(
    new CustomEvent<ComposerInsertDetail>(COMPOSER_INSERT_EVENT, { detail: { text } }),
  );
}

/**
 * 订阅插入请求，返回退订函数（必须调用，热更新/切页时否则会累积监听）。
 * 只在文本非空时回调：过滤规则与 {@link publishComposerInsert} 保持同一处判定，
 * 避免「发的时候过滤、收的时候不过滤」这种只在一边成立的不变量。
 */
export function subscribeComposerInsert(handler: (text: string) => void): () => void {
  const listener = (event: Event): void => {
    const text = (event as CustomEvent<ComposerInsertDetail>).detail?.text;
    if (typeof text === 'string' && text.length > 0) handler(text);
  };
  window.addEventListener(COMPOSER_INSERT_EVENT, listener);
  return () => window.removeEventListener(COMPOSER_INSERT_EVENT, listener);
}
