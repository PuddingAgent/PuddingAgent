# PuddingHtml — code_map

> **角色**：叶子组件（S1）。HTML 正文抽取 + HTML→Markdown 转换。
> **为什么存在**：`PuddingRuntime`（`http_fetch` 正文）与 `PuddingBrowser.AgentTools`（`browser_snapshot` 的 `text` 模式）
> 都需要同一套抽取，而这两者**互不引用**。任一方直接复用另一方都会造成反向依赖，
> 因此把共用部分下沉为叶子 —— 两侧都指向它，它不指向任何一方。
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
| `PuddingBrowser.AgentTools`（`browser_snapshot`） | `mode=text`（**尚未实现**，接入点在本组件） |

## 测试

`Source/PuddingHtmlTests`（只引用本组件）：抽取 8 例 / 转换 6 例 / 边界 6 例。
运行：`dotnet test Source\PuddingHtmlTests\PuddingHtmlTests.csproj`
