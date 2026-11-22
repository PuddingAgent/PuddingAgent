# PuddingBrowser.WebView2.Smoke · code_map
> 源指纹: *.cs=1-e240f374d2f2, *.csproj=1-342eecef780b · 条目数: 1 · 最近整理: 2026-11-22

WebView2 页面层在真实 WPF 消息泵下的端到端冒烟（net10.0-windows + WPF）。引用 `PuddingBrowser.Abstractions` 与 `PuddingBrowser.WebView2`。

**入口与配置**：`PuddingBrowser.WebView2.Smoke --url <http/https> [--data-root <dir>] [--hold-seconds 0..120]`。成功时向 stdout 打一条 JSON 事件并以 0 退出，失败打 `phase2a3-webview2-smoke-failed` 并以 1 退出。

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `Program.cs` | 真实 WebView2 下的页面会话冒烟 | `SmokeApplication`、`SmokeOptions`、`Program` | `Source/PuddingBrowser.WebView2/code_map.md` | 必须有真实窗口与消息泵；无宿主断言不了 ref 失效与版本推进 |

**项目约束**：`--data-root` 缺省落在系统临时目录下带 GUID 的子目录，冒烟不复用既有 profile。
