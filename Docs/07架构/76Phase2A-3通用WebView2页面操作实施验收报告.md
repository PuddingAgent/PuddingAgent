# 76 Phase 2A-3 通用 WebView2 页面操作实施验收报告

> - 状态：**automated accepted / real DeepSeek smoke pending（2026-08-02）**
> - 开发工作指令：[75 Phase 2A-3 工作指令](75Phase2A-3SnapshotLocatorInteractWait开发工作指令.md)
> - 前置阶段：[74 Phase 2A-2 验收报告](74Phase2A-2最小RemoteBrowser与AgentTools实施验收报告.md)
> - 适用范围：Windows 10/11、.NET 10、WPF、WebView2 Evergreen Runtime

## 1. 验收结论

Phase 2A-3 的通用页面观察和操作链路已经实现，并通过自动化测试、真实 WebView2 TestSite、Release publish 与可见 Desktop smoke：

```text
Agent Tool
  -> IBrowserRuntime / RemoteBrowserPage
  -> DesktopBrowserCommandBroker
  -> authenticated WebSocket Bridge
  -> BrowserWorkspaceController
  -> WebView2BrowserPage / WebView2DomClient
  -> 用户可见的隔离 WebView2 页面
```

本批新增 `browser_snapshot`、`browser_locate`、`browser_interact`、`browser_wait_for`，与 Phase 2A-2 的三项工具共同形成七项通用 Browser Tool。底层没有抖音选择器或个人 Chrome 依赖，也没有开放 Cookie、CDP、任意 Evaluate、下载或上传。

真实 DeepSeek Agent 决策 smoke 尚未执行：验收过程没有复制或读取用户 `D:\data` 中的 LLM 密钥，也没有为测试静默修改既有 Agent 的能力。该项是外部配置准入，不影响本批确定性实现和真实 WebView2 Driver 的自动验收，但进入 Douyin Adapter 前必须补做。

## 2. 交付结构

### 2.1 Abstractions 与 WebView2 Driver

| 文件/类 | 责任 |
|---|---|
| `PuddingBrowser.Abstractions/Locator.cs` | 增加 `LocatorKind.Ref` 和 `BrowserElementInfo`；Locator 仍为平台无关契约 |
| `PuddingBrowser.Abstractions/ScriptAndSnapshot.cs` | Snapshot 增加 `MaxDepth` 预算；Wait/交互配置保持通用 |
| `PuddingBrowser.WebView2/WebView2DomClient.cs` | Snapshot、Locator、八项交互、Wait、ref 分配和稳定领域错误 |
| `PuddingBrowser.WebView2/WebView2ElementHandle.cs` | 将 WebView2 元素元数据投影为 `IElementHandle` |
| `PuddingBrowser.WebView2/WebView2BrowserPage.cs` | 在 WPF Dispatcher 上串行调用 DOM Client，并维护 PageVersion |

Snapshot 预算在进入脚本前限制节点、文本和深度。用户 Locator、键值和表单输入通过 JSON 参数传入，不拼接到 JavaScript 源码。默认只输出可见节点，并递归 open shadow root 与同源 iframe；跨源 iframe 只保留边界。

每个可交互元素获得版本化 ref：

```text
v{PageVersion}-n{monotonic sequence}
```

ref 分配状态按 PageVersion 隔离，重复 Snapshot 不会把同一个 ref 分配给新节点。导航后旧版本 ref 在执行操作前返回 `stale_element_reference`。

### 2.2 Protocol、Desktop 与 Remote Proxy

新增 Bridge 命令：

```text
page.snapshot
page.locate
page.interact
page.waitFor
```

`BrowserWorkspaceController` 负责参数反序列化、Page 解析、WPF Page 调用和安全结果映射。`BrowserOperationException.Code` 原样跨 Bridge 返回，不降级为一般异常。

交互命令提交后不再次查询旧 Locator。click、press 或表单提交可能已经导航或替换 DOM；若提交后重查，会把已成功的副作用误报为 stale/not-found，并诱发 Agent 重试。调用方必须用 `browser_wait_for` 或新 Snapshot 获取提交后的状态。

`RemoteBrowserPage` 只依赖 Broker 和协议 DTO，不引用 Desktop/WebView2。Core 重启后可重新列举 Desktop 持有的 Context/Page；代理释放不会关闭 Desktop 页面。

### 2.3 Agent Tools 与能力

| Tool | 主要输入 | 主要输出 |
|---|---|---|
| `browser_snapshot` | Context/Page、节点/文本/深度预算 | Page、DOM/AX 摘要、NodeCount、Truncated |
| `browser_locate` | Context/Page、Locator | 最多 100 个元素、ref、role/name/text/状态/边界框 |
| `browser_interact` | action、Locator/ref、动作参数 | action、Page；不回显 fill/type 值 |
| `browser_wait_for` | selector/selector_hide/url、timeout | completed/timedOut、Page |

四项工具只通过 `IBrowserRuntime` 工作。仅 `DesktopChild && BrowserAutomationEnabled` 注册全部七项 Browser Tool；Console/disabled Host 为 0。新建通用助手模板包含七个 `cap-browser-*`，既有 Agent 不会被静默扩权。

Activity 只记录命令名、Context/Page 摘要、时间和错误码，不记录完整 Arguments、页面文本、ControlToken 或表单值。

## 3. 稳定错误语义

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

Agent 必须根据 `error.code` 分支：stale 重新 Snapshot；not-found/ambiguous 调整 Locator；unsupported 不重试相同动作。错误消息仅用于短诊断。

## 4. 真实 TestSite 与 smoke

新增：

- `Tests/PuddingBrowser.TestSite`：表单、label/role/placeholder/test-id/text、动态替换、隐藏元素、select、checkbox、open shadow root、同源 iframe、popup；
- `Tests/PuddingBrowser.WebView2.Smoke`：真实 WPF + WebView2 Runtime；
- `TestScripts/start-phase2a3-webview2-smoke.ps1`：动态 Loopback TestSite、系统 Temp DataRoot、进程所有权和退出清理。

真实 WebView2 smoke 覆盖：

1. Snapshot 并按 Role 定位 Save；
2. fill、type、press、hover、window scroll，每一步由 TestSite DOM 状态断言；
3. select、check、按 ref click；
4. wait `#saved`，再次 Snapshot 看到 `Saved`；
5. 导航到 `/frame`，旧 `v1-*` ref 返回 `stale_element_reference`；
6. WPF/WebView2/TestSite 全部退出。

最后一次通过证据：

```json
{"event":"phase2a3-webview2-smoke-passed","pageVersion":2,"NodeCount":30,"Truncated":false,"saveRef":"v1-n4","finalContainsSaved":true,"staleCode":"stale_element_reference"}
```

WebView2 退出后可能短暂锁住 UDF 文件。smoke 清理只允许 `%TEMP%\PuddingAgent\phase2a3-webview2-*`，最多重试 5 秒；清理失败只报告 Warning，不能把已经通过的页面断言改判为失败。

## 5. 自动化结果

2026-08-02 串行执行结果：

| 验收项 | 结果 |
|---|---:|
| `PuddingBrowser.AgentTools.Tests` | 10/10 passed |
| `PuddingHost.Tests` | 56/56 passed |
| `PuddingDesktop.Tests` | 102/102 passed |
| `BuiltInAgentTemplatesTests`（定向） | 3/3 passed |
| AgentTools/Desktop/TestSite/Smoke 定向构建 | 0 error |
| 真实 WebView2 TestSite smoke | passed |
| Desktop Release publish | passed |
| 发布版 Desktop 可见 smoke | exitCode 0，remaining children 0 |

Release 发布目录为仓库临时输出 `.tmp-build/phase2a3-final`，包含：

```text
PuddingDesktop.exe
core/PuddingAgent.exe
core/PuddingBrowser.AgentTools.dll
core/PuddingBrowser.Abstractions.dll
core/PuddingBrowser.Protocol.dll
core/wwwroot/admin/index.html
core/default-data/agent-template-presets/general-assistant.json
```

发布版 smoke 中，Desktop 从 UI 启动 `core/PuddingAgent.exe --desktop-child`，Agent Browser 显示 Bridge `Connected`，创建真实 `about:blank` 标签页；从标题栏明确退出后 Desktop exitCode 为 0，Core 子进程无残留。

Publish 中仍有仓库既有 NuGet 安全和 nullable/analyzer warnings；本批定向项目均为 0 error，未以隐藏 warning 的方式修改构建门槛。

## 6. 未交付边界

本批明确没有实现：

- Douyin 页面选择器、登录流程、评论业务 DTO；
- Cookie/Storage 读写；
- 任意 `EvaluateAsync` Agent Tool；
- 原生 CDP/Network 拦截；
- 下载、上传、文件选择；
- drag、跨源 iframe 深入访问；
- BrowserWindow 弹窗承载和 Surface 转移。

这些能力必须逐项评审，不能因为 WebView2 给 Agent 使用就绕开可观察性、错误语义和数据目录隔离。

## 7. 下一步工作指令

下一批为 **Phase 2A-3B 真实 DeepSeek Agent 可见 smoke 与工具选择验收**，暂不进入 Douyin Adapter：

1. 由用户明确选择一个已配置 DeepSeek 的测试 DataRoot 和测试 Agent；不得复制、输出或写入 LLM Secret；
2. 测试 Agent 显式授予全部七项 `cap-browser-*`，保存后重新加载 Agent 配置；
3. 通过 `PuddingDesktop.exe` 启动 Core，在 Agent Browser 创建 TestSite 页面并交给 Agent；
4. 给 Agent 一个只描述结果、不描述工具顺序的任务：填写姓名、选择 Designer、勾选条款、保存并报告 `Saved`；
5. 验证实际调用包含 snapshot/locate/interact/wait，且复用 context/page/ref；stale 后会重新 Snapshot，不重复已提交动作；
6. 保存脱敏的 Tool call 顺序、Bridge Activity、最终页面截图、模型/provider/role 和退出结果；不得保存表单值、Token、Cookie 或 API Key；
7. 验收通过后再编写 Phase 2A-4 Douyin Adapter 工作指令，Douyin 层只能依赖通用 Browser Tools。

若用户没有提供可安全使用的测试 Agent/DataRoot，本项保持 `pending`，不得读取真实运行目录中的密钥来“自动完成”验收。
