# PuddingBrowser.TestSite · code_map
> 源指纹: *.cs=1-632d8b7df950, *.csproj=1-56e764e04ec9 · 条目数: 3 · 最近整理: 2026-11-22

浏览器验收用的最小站点（ASP.NET Core minimal API）。被 `PuddingBrowser.WebView2.Smoke` 与桌面部件的验收项当作观察对象。

**入口与配置**：`dotnet run` 后按 `Properties/launchSettings.json` 监听 7064/7065；`GET /health` 供就绪探测。

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `Program.cs` | 暴露 fixture 路由与交互页面 | `WebApplication`、`Fixture` | `fixtures/` | 页面 DOM 是验收锚点，改动须同步验收断言 |
| `fixtures/` | 浏览器验收观察对象的结构化 HTML 样本 | `serp-zh.html`、`serp-en.html`、`article.html`、`scroll.html`、`events.html` | `Program.cs` | 以文件而非内联字符串提供；内联会失去结构断言锚点 |
| `Properties/launchSettings.json` | 本地启动配置与监听端口 | — | `Program.cs` | 端口被外部验收脚本引用，改动须同步 |

**项目约束**：fixture 随站点进输出目录（csproj 的 `Content` 复制项），删除该复制项会让 `/fixtures/*` 在测试宿主里 404。
