# 75 Phase 2A-3 Snapshot、Locator、Interact、Wait 开发工作指令

> - 状态：**automated accepted / real DeepSeek smoke pending（2026-08-02）**
> - 前置验收：[74 Phase 2A-2 验收报告](74Phase2A-2最小RemoteBrowser与AgentTools实施验收报告.md)
> - 实施验收：[76 Phase 2A-3 验收报告](76Phase2A-3通用WebView2页面操作实施验收报告.md)
> - 目标：让通用 Agent 能观察并操作 Desktop WebView2 页面
> - 禁止混入：Douyin 选择器、Cookie、CDP、Evaluate、下载、上传和 BrowserWindow

## 1. 本批交付

新增四个 Bridge 命令面和四个 Agent Tool：

```text
page.snapshot  -> browser_snapshot
page.locate    -> browser_locate
page.interact  -> browser_interact
page.waitFor   -> browser_wait_for
```

底层仍使用 `IBrowserPage`，AgentTools 不引用 WPF/WebView2，Remote proxy 不引用 Desktop UI。

## 2. Snapshot 契约

`SnapshotOptions` 增加 `MaxDepth`，所有预算必须在进入 WebView2 前归一化：

```text
MaxNodes      1..10000，默认 5000
MaxTextLength 256..500000，默认 200000
MaxDepth      1..64，默认 24
```

Snapshot 返回 `DomText`、`AccessibilityTree`、可选 `Html`、`NodeCount` 和 `Truncated`。默认不输出隐藏节点；同源 iframe 和 open shadow root 可递归，跨源 iframe只输出边界节点。

可交互节点获得 ref：

```text
v{PageVersion}-n{sequence}
```

ref 写入当前文档 DOM 的 `data-pudding-ref`。`LocatorKind.Ref` 只接受上述格式；ref 中版本与当前 `PageVersion` 不同返回 `stale_element_reference`。普通 CSS/Role/Text 等 Locator 每次重新解析，不受旧 ref 约束。

## 3. Locator

V1 支持：

```text
Ref, Css, XPath, Text, Role, Label, Placeholder, AltText, Title, TestId
```

支持 `Exact`、`Nth`、`HasText`；`Frame`、复合 `Has` 在本批可返回 `browser_operation_not_supported`，不能静默忽略。

`browser_locate` 返回匹配数量以及每个匹配的 ref、tag、role、name、text、visible、enabled、checked 和 bounding box。返回最多 100 个匹配，超限给出 warning。

## 4. Interact

本批动作：

```text
click | fill | type | press | hover | scroll | select | check
```

`drag` 和 `upload` 继续返回 `browser_operation_not_supported`。

- click/fill/type/press/hover/select/check 必须提供 Locator 或 ref；
- scroll 可不提供 Locator，默认滚动 window；
- Locator 0 匹配返回 `browser_element_not_found`；
- 多匹配且未指定 Nth 返回 `browser_locator_ambiguous`；
- disabled/hidden 等不可操作状态返回稳定领域错误；
- Activity 不记录 fill/type 的 value，不记录完整 Locator 或页面文本。

## 5. Wait

`browser_wait_for` 支持：

```text
selector       CSS 出现
selector_hide  CSS 消失或隐藏
url            wildcard URL 匹配
timeout_ms     1..120000
```

等待使用可取消的短轮询，不能在 WPF UI 线程同步阻塞。超时返回正常 `WaitResult { TimedOut=true }`，调用方取消继续抛出 `OperationCanceledException`。

## 6. 错误码

```text
browser_invalid_arguments
browser_page_not_found
browser_element_not_found
browser_locator_ambiguous
browser_element_not_visible
browser_element_disabled
stale_element_reference
browser_operation_not_supported
browser_operation_failed
```

异常文本只用于短诊断；Agent 分支判断必须依赖 code。

## 7. 测试矩阵

### Abstractions/WebView2

- Locator/ref 版本解析；
- Snapshot options 预算归一化；
- ExecuteScript JSON 双重编码解析；
- Snapshot/locator/interact 脚本不拼接用户输入；
- stale ref 在执行脚本前失败。

### Protocol/Desktop

- 四个命令进入 `BrowserBridgeCommandNames.All` 和 source-generated JSON context；
- Controller 将 Snapshot、Locate、Interact、Wait 转给正确 Page；
- Browser domain error code 原样返回，不降级为 `browser_operation_failed`；
- Activity 只显示安全动作摘要。

### Remote/AgentTools

- Remote page 映射四个命令和结果；
- 四个新工具描述符、必填参数、成功结构；
- invalid/ambiguous/stale/cancellation 语义；
- DesktopChild 注册七个 Browser Tools，Console/disabled 为 0。

### TestSite

本地 TestSite 至少包含：

- label/input/button/form；
- role、placeholder、test-id、text Locator；
- 动态替换 DOM，用于 stale ref；
- hidden 元素；
- select、checkbox；
- 同源 iframe；
- popup 按钮。

## 8. 验收命令

构建和测试串行执行，输出放仓库 `.tmp-build` / `.tmp-test-out` 或系统 Temp：

```powershell
dotnet build Source\PuddingBrowser.Protocol\PuddingBrowser.Protocol.csproj --no-restore --nologo
dotnet build Source\PuddingBrowser.WebView2\PuddingBrowser.WebView2.csproj --no-restore --nologo
dotnet build Source\PuddingBrowser.AgentTools\PuddingBrowser.AgentTools.csproj --no-restore --nologo
dotnet build Source\PuddingHost\PuddingHost.csproj --no-restore --nologo
dotnet build Source\PuddingDesktop\PuddingDesktop.csproj --no-restore --nologo

dotnet test Tests\PuddingBrowser.AgentTools.Tests\PuddingBrowser.AgentTools.Tests.csproj --no-restore --nologo
dotnet test Tests\PuddingHost.Tests\PuddingHost.Tests.csproj --no-restore --nologo
dotnet test Tests\PuddingDesktop.Tests\PuddingDesktop.Tests.csproj --no-restore --nologo

dotnet publish Source\PuddingDesktop\PuddingDesktop.csproj `
  -c Release --no-restore -o .tmp-build\phase2a3-preview --nologo

git diff --check
```

可见 smoke 必须使用系统 Temp 隔离 DataRoot。真实 DeepSeek smoke 只有在不复制、不回显用户 LLM Secret，并且使用用户明确配置的测试 Agent 时才执行；否则报告为待验收，不得伪造。

## 9. Definition of Done

- [x] Snapshot 有节点、文本、深度预算和 truncation 元数据。
- [x] Snapshot ref 包含 PageVersion，旧 ref 稳定失败。
- [x] 八项 Interact 和 Wait 可通过真实 WebView2 TestSite。
- [x] 四项 Agent Tools 只通过 `IBrowserRuntime` 工作。
- [x] Tool → authenticated Bridge → Desktop Page 集成测试通过。
- [x] Console/disabled 不暴露七项 Browser Tools。
- [x] 发布包与可见 Desktop smoke 通过且退出无残留。
- [x] 真实 DeepSeek smoke 的外部配置准入已明确记录在 76；模型 smoke 本身仍 pending。
- [x] 文档、Agents、code_map、How-Debuge 状态一致。
