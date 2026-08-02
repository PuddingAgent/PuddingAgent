# Phase 2A-3B：真实 DeepSeek Agent 浏览器工具选择验收工作指令

> 状态：待执行  
> 执行入口：`PuddingDesktop.exe`，不得以 `dev-up.py` 代替产品链路  
> 前置结果：[76Phase2A-3通用WebView2页面操作实施验收报告](76Phase2A-3通用WebView2页面操作实施验收报告.md)  
> 通过后下一步：只编写 Phase 2A-4 Douyin Adapter 工作指令，不在本批实现抖音业务代码

## 1. 本批唯一目标

使用一个由用户明确选择、已经配置 DeepSeek 模型的测试 Agent，通过真实 `PuddingDesktop -> Core -> Agent Runtime -> Browser Tools -> authenticated BrowserBridge -> WebView2` 链路，自主完成本地 TestSite 表单任务。

本批验证的是模型能否在只收到“目标描述”的情况下正确选择工具、复用页面身份、观察结果并安全结束，不再重复证明底层 DOM 脚本的确定性正确性。

验收必须回答五个问题：

1. 实际调用是否确实由 DeepSeek provider/model 完成，且没有静默 fallback；
2. Agent 是否先观察页面，再定位和操作，而不是猜测 DOM；
3. Agent 是否复用同一个 `context_id` / `page_id` / `page_version`；
4. 页面是否真实出现 `Saved`，Agent 是否在观察到结果后才报告完成；
5. Tool、Bridge Activity、日志和证据中是否没有 Token、Cookie、API Key 或真实表单值。

## 2. 强制边界

- 不实现 Douyin Adapter、抖音选择器、登录或评论 DTO。
- 不新增任意 `EvaluateAsync`、Cookie/Storage、CDP、Network、上传或下载能力。
- 不读取、复制、打印或写入 LLM Secret；只记录 provider ID、model ID 和路由角色。
- 不复制用户 DataRoot。使用用户明确选择的测试 DataRoot，但只通过现有配置界面/API读取非敏感元数据。
- 不直接修改既有 Agent 来扩大权限。若测试 Agent 缺能力，由用户在界面显式授予，或创建独立测试 Agent。
- 不清理、重置或迁移 `D:\data` 数据库。
- 不允许模型通过 shell、HTTP 或 JavaScript 绕过 Browser Tools 操作 TestSite。
- Pudding 内部开发 Agent 不得启动第二个 Desktop 并把它当成新版本 smoke；单实例只会激活当前旧进程。
- Pudding 内部 Agent 可以测试当前进程已经加载的 Browser Tools、Bridge 命令、TestSite 页面结果和模型工具选择；但不得据此证明刚修改的程序集已经加载。
- Pudding 内部 Agent 不得独立对 Desktop 启动、升级到新构建、Core 进程所有权、崩溃恢复或退出回收声明验收通过。
- 执行 Agent 不得尝试关闭承载自己的 `PuddingDesktop`；完整产品验收只能由 Pudding 进程外的独立控制器执行。
- 保留当前 dirty worktree，不回退或覆盖无关修改。

## 3. 执行角色

本批必须区分三个角色：

- **内部开发 Agent**：运行在旧/当前 Pudding 中；负责代码修改、独立构建/测试和生成外部部署交接；
- **外部测试控制器**：运行在 PuddingDesktop/Core 进程之外，例如 Codex、Visual Studio Test Runner 或人工 PowerShell；唯一有权启动/停止目标 Desktop、确认加载的是新构建并判定产品 smoke；
- **产品内测试 Agent**：运行在外部控制器启动的新 Pudding 中；可以真实测试已经加载的代码和工具，在新的独立会话里只接收第 6 节目标提示并生成会话内功能证据，但不能关闭自己的宿主或给出最终生命周期结论。

内部开发 Agent 与外部测试控制器不能是同一个运行实例。不得把当前 Pudding Agent 启动第二个 Desktop或重启自身当成新构建证据；但在外部控制器明确重启到新构建后，可以让 Pudding 内的新测试会话执行工具功能 smoke。不得把阅读过本文件的开发会话伪装成“无提示工具选择”测试会话。

### 3.1 内部开发 Agent 的停止点

第一次停止点之前，内部开发 Agent 可以完成：

- 冻结 changed-file 边界；
- 修改通用 Browser/Desktop 代码；
- 运行单元、集成和独立 TestSite/WebView2 smoke 进程；
- 生成脱敏的测试配置说明和 `internal-handoff.json`；
- 报告 `ready-for-external-deploy`。

到达该状态后必须停止，等待外部控制器：

- 退出当前 Pudding；
- 确认旧 Desktop/Core PID 已消失；
- 构建或启动目标版本 `PuddingDesktop.exe`；
- 确认目标 Desktop/Core 的实际映像路径、PID、Ready 和 Bridge Connected。

外部部署完成后，产品内测试 Agent 可以继续：

- 在独立新会话中执行第 6 节真实 DeepSeek 任务；
- 测试当前已加载的 Browser Tools、Bridge 和 TestSite；
- 写入脱敏 tool sequence、Activity、Saved 页面结果；
- 报告 `in-product-functional-complete` 后停止。

最后由外部控制器断开/重启 Core、退出 Desktop、观察进程所有权，并写入最终 `passed|failed|blocked` 结论。

## 4. Preflight

### 4.1 冻结边界

先记录但不清理：

```powershell
git status --short
Get-Process PuddingDesktop,PuddingAgent -ErrorAction SilentlyContinue |
  Select-Object ProcessName,Id,Path
```

不得停止用户未明确纳入本次 smoke 的 Desktop/Core 进程。

### 4.2 确定性门槛

串行执行：

```powershell
dotnet build Tests\PuddingBrowser.TestSite\PuddingBrowser.TestSite.csproj --no-restore --nologo
dotnet build Tests\PuddingBrowser.WebView2.Smoke\PuddingBrowser.WebView2.Smoke.csproj --no-restore --nologo
dotnet test Tests\PuddingBrowser.AgentTools.Tests\PuddingBrowser.AgentTools.Tests.csproj --no-restore --nologo
dotnet test Tests\PuddingHost.Tests\PuddingHost.Tests.csproj --no-restore --nologo
dotnet test Tests\PuddingDesktop.Tests\PuddingDesktop.Tests.csproj --no-restore --nologo
powershell -ExecutionPolicy Bypass -File TestScripts\start-phase2a3-webview2-smoke.ps1 -HoldSeconds 0
```

任何新增失败必须先区分本批回归与仓库既有 warning。不得跳过失败继续消耗真实模型调用。

### 4.3 产品链路

本节的启动和新构建身份确认只能由外部测试控制器执行。目标 Pudding 启动后，产品内测试 Agent可以观察并测试 Ready、Bridge 和 Browser Tools，但不能替代外部进程身份记录：

1. 启动最新构建的 `PuddingDesktop.exe`；
2. 运行中心必须显示 Core Ready、动态 Loopback 地址和健康检查正常；
3. Agent Browser 必须显示 Bridge Connected；
4. 若显示“自动恢复已熔断”，先退出并重新启动最新 Desktop，不能用连续点击“启动”掩盖根因；
5. Core 日志不得出现 `DirectoryNotFoundException: Source\PuddingAgent\wwwroot`。

### 4.4 测试 Agent

通过现有 UI 或非敏感配置 DTO 确认：

- provider/model 均为用户明确选择的 DeepSeek 路由；
- 不允许本轮 fallback 到其他 provider/model；
- Agent 已具有以下 capability：

```text
cap-browser-context
cap-browser-tabs
cap-browser-navigate
cap-browser-snapshot
cap-browser-locate
cap-browser-interact
cap-browser-wait-for
```

如果缺少 DeepSeek 配置或用户未指定测试 Agent/DataRoot，状态写为 `blocked` 并报告缺少哪项；不得打开 provider 配置文件寻找密钥。

## 5. 准备真实 TestSite 页面

TestSite 可以由内部开发 Agent预构建，但正式验收实例及其进程所有权由外部测试控制器管理。使用独立终端启动站点：

```powershell
dotnet run --project Tests\PuddingBrowser.TestSite\PuddingBrowser.TestSite.csproj `
  --no-build --urls http://127.0.0.1:0
```

从 `Now listening on:` 读取动态 Loopback URL。随后：

1. 在 Agent Browser 创建一个真实 WebView2 标签页；
2. 导航到 TestSite URL；
3. 确认页面标题为 `Browser automation test`；
4. 将该页设为测试 Agent 的 target；
5. 记录 `context_id` 和 `page_id`，但不向测试 Agent提供工具调用顺序。

TestSite 必须使用本仓库项目和 Loopback 动态端口，不能换成公网网页。

## 6. 发送给测试 Agent 的唯一任务提示

在新的独立会话中只发送下面这段话：

```text
请在当前分配给你的 Agent Browser 页面中完成资料表单：姓名填写“Pudding Browser Smoke”，角色选择“Designer”，勾选接受条款并保存。请在页面明确出现“Saved”之后告诉我结果；请自行决定如何完成，不要让我手动点击。
```

这些值是专用合成测试数据，不得替换为用户真实姓名或账号信息。不得追加“先调用 snapshot/locate/interact/wait”等工具顺序提示。

模型最多允许两次完整尝试。第一次失败后必须先记录稳定错误码和原因，再决定是否进行第二次；不得无限重试消耗 Pro 模型额度。

## 7. 通过标准

### 7.1 模型与工具选择

- 路由证据显示实际 provider/model 为选定 DeepSeek，role/profile 符合测试 Agent；
- `browser_snapshot` 在首次 DOM 修改前调用；
- 调用链至少包含 `browser_snapshot`、`browser_locate`、`browser_interact` 和 `browser_wait_for`；
- Agent 没有使用 shell、HTTP、文件修改或任意脚本直接操作页面；
- 同一任务不创建无关 Context/Page，不在每一步重新建标签页；
- 所有后续调用复用工具返回的 `context_id`、`page_id` 和当前 `page_version`；
- 若发生 `stale_element_reference`，下一步必须重新 Snapshot；没有发生 stale 不作为失败，确定性 stale 已由 Phase 2A-3 smoke 覆盖；
- 已成功提交的动作不能在错误恢复后重复执行。

### 7.2 页面结果

- Name、Role、Terms 的最终 DOM 状态符合合成测试任务；
- `#saved` 真实可见，且 `browser_wait_for` 返回成功；
- Agent 最终回复明确说明已完成，不能在 Saved 出现前提前宣告成功。

### 7.3 安全与可观察性

- Bridge Activity 只显示动作摘要、page/ref 和稳定错误码；
- fill/type 的值不出现在 Bridge Activity、Desktop 诊断日志或汇总 JSON；
- 不记录 Token、Cookie、Authorization、API Key 或 provider endpoint secret；
- 截图只保留 Agent Browser 状态、Activity 和 `Saved` 结果区域，裁掉或遮挡输入框内容；
- 真实用户 Chrome/Profile 不受影响。

## 8. 验收证据

写入：

```text
.tmp-test-out/phase2a3b-deepseek-smoke/<timestamp>/
```

至少包含：

```text
internal-handoff.json
summary.json
tool-sequence.sanitized.json
bridge-activity.png
final-saved.png
preflight.txt
shutdown-observation.json
```

内部开发 Agent 第一次只写 `internal-handoff.json`，其状态必须为 `ready-for-external-deploy`，建议结构：

```json
{
  "status": "ready-for-external-deploy",
  "sourceRevision": "working-tree",
  "desktopExecutable": "absolute path",
  "completedChecks": ["build", "unit-tests", "isolated-webview2-smoke"],
  "notVerified": ["desktop-start", "bridge-reconnect", "deepseek-tool-selection", "desktop-exit-cleanup"]
}
```

它不能提前创建内容为 `passed` 的 `summary.json`。外部部署后，产品内测试 Agent可以补充工具调用与页面结果证据，并将状态更新为 `in-product-functional-complete`；最终 `summary.json` 的结论和生命周期证据只能由外部测试控制器写入。

`summary.json` 使用以下结构，不保存工具参数原文：

```json
{
  "phase": "2A-3B",
  "status": "passed|failed|blocked",
  "agentId": "test-agent-id",
  "conversationId": "conversation-id",
  "providerId": "deepseek-provider-id",
  "modelId": "deepseek-model-id",
  "contextId": "context-id",
  "pageId": "page-id",
  "toolSequence": [
    { "name": "browser_snapshot", "success": true, "errorCode": null }
  ],
  "savedVisible": true,
  "attemptCount": 1,
  "secretRead": false,
  "sensitiveValueInActivity": false
}
```

证据可从现有会话 Tool 卡片、运行时事件和 Browser Activity 中提取。不得为方便验收新增一套生产遥测或读取原始 provider secret。

## 9. 退出与自宿主限制

运行在 Pudding 内部的开发 Agent 无法可靠测试自己的新构建：当前进程仍加载旧程序集，Desktop 单实例会拦截第二个实例，关闭自身后 Agent/Core/Bridge 同时消失，无法继续记录退出结果。这是验收架构边界，不应通过重试或后台自杀脚本规避。

退出验收按以下方式完成：

1. 内部开发 Agent 写入 `internal-handoff.json` 并停止在 `ready-for-external-deploy`；
2. 外部测试控制器要求用户退出当前 Pudding，记录旧 Desktop/Core PID 已消失；
3. 外部控制器启动明确路径下的新 Desktop，并记录实际进程映像路径与 PID；
4. 产品内测试 Agent 完成浏览器任务和会话内功能证据，写入 `in-product-functional-complete`；
5. 外部控制器根据需要执行 Core Stop/Start/Restart 与 Bridge 重连观察；
6. 外部控制器通过托盘或明确退出路径关闭目标 Desktop；
7. 外部观察进程记录 Desktop 退出码以及它拥有的 Core/WebView2 子进程残留；
8. 外部控制器写入 `shutdown-observation.json` 和最终 `summary.json`。

通过条件：Desktop 正常退出，原 Core PID 不存在，未残留由本次 Desktop 创建的 WebView2 子进程。不得通过杀死所有 `msedgewebview2` 进程来制造通过结果。

## 10. 缺陷处理规则

- 如果是模型选择或提示问题：保留第一次证据，最多调整一次测试 Agent 的非敏感 system/tool 描述后重试；
- 如果是通用 Browser Tool、Bridge 或 Desktop 缺陷：只在通用层修复，并补对应自动化回归测试；
- 如果是 TestSite 缺陷：只修改 TestSite，不在生产代码增加测试专用分支；
- 如果是 DeepSeek provider 不可用、额度不足或用户未授权：标记 `blocked`，不切换其他模型伪造验收；
- 如果只是 Douyin 页面差异：本批不处理，因为尚未进入 Douyin Adapter。

修复后必须重新运行受影响项目的定向测试和真实 WebView2 smoke，再允许第二次模型尝试。

## 11. Definition of Done

- [ ] 用户明确选择了测试 DataRoot、测试 Agent 和 DeepSeek provider/model。
- [ ] 七项 Browser capability 已通过 UI/DTO 确认，没有读取 Secret。
- [ ] 内部开发 Agent 已完成确定性测试并写入 `ready-for-external-deploy`，没有自称新构建已加载。
- [ ] 外部测试控制器确认产品入口为最新 `PuddingDesktop.exe`，Core Ready、Bridge Connected。
- [ ] 产品内测试 Agent 已对当前加载版本完成真实工具 smoke 并写入 `in-product-functional-complete`。
- [ ] 新会话只收到结果导向任务提示。
- [ ] DeepSeek 实际调用了 Snapshot、Locate、Interact、Wait。
- [ ] Context/Page/PageVersion 正确复用，错误恢复没有重复提交动作。
- [ ] TestSite 最终显示 `Saved`，Agent 在观察后报告完成。
- [ ] 脱敏证据齐全，Activity 和日志没有敏感值。
- [ ] 用户明确退出后 Core/WebView2 进程所有权验收通过。
- [ ] 最终 `passed|failed|blocked` 结论由 Pudding 进程外控制器写入。
- [ ] 76、77、`Docs/README.md`、`Docs/07架构/README.md` 和 `Source/code_map.md` 已更新为一致状态。
- [ ] 通过后只提交 Phase 2A-4 Douyin Adapter 工作指令，不在本批混入实现。

## 12. 最终汇报格式

```text
Phase 2A-3B status: passed | failed | blocked
Verifier: external process/controller identity
Internal handoff: ready-for-external-deploy=<true|false>, in-product-functional-complete=<true|false>
DeepSeek route: <providerId>/<modelId>, fallback=<none|details>
Agent/session: <agentId>/<conversationId>
Browser identity: <contextId>/<pageId>, pageVersions=<...>
Tool sequence: <sanitized names + stable error codes>
Final DOM: Saved visible=<true|false>
Attempts: <1|2>
Safety: secret read=<false>, sensitive value in activity=<false>
Shutdown: Desktop exit=<code>, remaining owned children=<count>
Evidence: <absolute evidence directory>
Changed files: <only files changed by this batch>
Next gate: Phase 2A-4 instruction allowed=<yes|no>
```
