# PuddingHtml — code_map

> **角色**：叶子组件（S1）。HTML 正文抽取 + HTML→Markdown 转换。
> **为什么存在**：`PuddingRuntime` 的 `http_fetch` 需要正文抽取与 Markdown 转换。这两个类原先就住在
> `PuddingRuntime` 里；抽成叶子是为了让它们**能脱离宿主测试**（组件化交付规程的首要目的是可测），
> 而不是因为当时已有第二个消费方。
>
> ⚠️ **勘误（2026-10-24）**：抽离时的动机写成“浏览器 `mode=text` 也需要它”，这个判断是**错的**。
> `browser_snapshot` 的 `text` 取的是驱动侧 `ContentBlocks` 语义块，由 `ArticleMarkdown` /
> `ArticleCompactJson` 组装，**不经过 HTML 解析**。误判的原因：`BrowserReadingMode.Text` 上挂了一条
> 陈旧注释写着“尚未实现”（已在同轮修正）；实际 `TryBuildReadingRequest` 对 `Text` 直接放行，
> 实现完整存在。教训：**工具描述/注释也能把人带坑——判断“缺什么”必须先看代码路径是否可达**。
> **只在本组件内维护**：改完代码只更新本文件受影响条目。

## 边界（机器可验，见 `ComponentBoundaryTests`）

| 规则 | 断言 |
|---|---|
| 零 `<ProjectReference>` | `PuddingHtml_Csproj_Declares_No_ProjectReference` |
| 程序集不引用任何 `Pudding*` | `PuddingHtml_Assembly_References_No_Pudding_Assembly` |
| 包引用仅限解析库 | `PuddingHtml_Csproj_Keeps_Package_Surface_Narrow` |
| 测试无需宿主 | `TestProcess_Does_Not_Load_Host_Or_Browser_Assemblies` |

允许的包：`HtmlAgilityPack`（DOM 解析）、`ReverseMarkdown`（Markdown 产出）。
**不得**引入日志、DI、宿主抽象 —— 那会让叶子把宿主重新拖回来。

## 文件

| 文件 | 职责 | 关键符号 |
|---|---|---|
| `HtmlContentExtractor.cs` | 抽出可读正文；纯文本化；HTML 判定 | `ExtractReadableHtml` / `ToPlainText` / `IsHtml` |
| `HtmlToMarkdownConverter.cs` | 转换契约 + ReverseMarkdown 实现（GFM、去注释、URL scheme 白名单） | `IHtmlToMarkdownConverter` / `HtmlToMarkdownConverter` |

## 抽取策略（现状与已知取舍）

- 噪声标签整棵移除：`script/style/nav/footer/header/noscript/svg/canvas/iframe/form`。
- 正文容器优先级：`//main` → `//article` → `//body` → 文档根。
- **已知取舍**：这是「地标优先 + 噪声剔除」，**不是** Readability 式的内容密度打分。
  对无地标、侧栏很重的页面会保留较多非正文。要升级就该在这里加密度启发式，
  并补一条「侧栏占多数时正文仍被选中」的用例。

## 消费方

| 消费方 | 用法 |
|---|---|
| `PuddingRuntime`（`http_fetch`） | `HttpFetchContentFormatter` 的 `markdown`/`text`/`outline` 三种表示；DI 注册 `HtmlToMarkdownConverter` |
| `PuddingBrowser.AgentTools`（`browser_snapshot`） | **不消费本组件**。`mode=text` 走驱动的 `ContentBlocks` 语义块，由 `ArticleMarkdown` / `ArticleCompactJson` 组装；浏览器侧不重新解析 HTML |

## 测试

`Source/PuddingHtmlTests`（只引用本组件）：抽取 8 例 / 转换 6 例 / 边界 6 例。
运行：`dotnet test Source\PuddingHtmlTests\PuddingHtmlTests.csproj`
