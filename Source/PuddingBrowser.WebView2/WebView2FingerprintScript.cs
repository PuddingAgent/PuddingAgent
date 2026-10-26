namespace PuddingBrowser.WebView2;

/// <summary>
/// **指纹对齐脚本**：把 WebView2 宿主留下的、普通 Edge 浏览器不会有的痕迹抹掉。
///
/// 实测（2026-10-26，本机 9222 端点，douyin 页面）：
/// UA = <c>… Chrome/156.0.0.0 Safari/537.36 Edg/156.0.0.0</c>（与真实 Edge 一致）、
/// <c>navigator.webdriver = false</c>、PDF 插件 5 个、语言 zh-CN —— 这些都正常；
/// 唯一暴露宿主身份的是 <c>window.chrome</c> 上多了 <c>webview</c> 键
/// （真实 Edge 只有 loadTimes / csi / app）。
///
/// 注意边界：这里**只**抹掉"宿主造成的差异"，不伪造 UA、不改 hardwareConcurrency、
/// 不注入假插件 —— 那些是浏览器真实提供的事实，改它们等于制造新的不一致。
/// </summary>
internal static class WebView2FingerprintScript
{
    public const string Source = """
        (() => {
          try {
            const c = window.chrome;
            if (c && 'webview' in c) {
              try { Object.defineProperty(c, 'webview', { get() { return undefined; }, configurable: true }); }
              catch (e) { try { delete c.webview; } catch (e2) { /* 不可删就留给页面，不抛错 */ } }
            }
          } catch (e) { /* 注入脚本绝不能让页面脚本失败 */ }
        })();
        """;
}
