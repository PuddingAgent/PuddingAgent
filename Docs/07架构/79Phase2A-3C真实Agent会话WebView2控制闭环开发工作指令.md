# Phase 2A-3C：真实 Agent 会话到 WebView2 控制闭环开发工作指令

> 状态：待开发  
> 执行对象：Pudding 内部开发 Agent  
> 前置基线：[76Phase2A-3通用WebView2页面操作实施验收报告](76Phase2A-3通用WebView2页面操作实施验收报告.md)  
> 功能验收：[77Phase2A-3B真实DeepSeekAgent浏览器工具选择验收工作指令](77Phase2A-3B真实DeepSeekAgent浏览器工具选择验收工作指令.md)

## 1. 本批结论与目标

下一步是完成“真实 Agent 控制 WebView2”的产品闭环，但**不是重新实现 BrowserBridge 或 Playwright 兼容层**。

当前代码已经具备：

- Desktop 中隔离于用户 Chrome 的真实 WebView2 Browser Workspace；
- Core 与 Desktop 之间经过控制令牌认证的 WebSocket BrowserBridge；
- Core 侧 `RemoteBrowserRuntime` / `RemoteBrowserContext` / `RemoteBrowserPage`；
- `browser_context`、`browser_tabs`、`browser_navigate`、`browser_snapshot`、`browser_locate`、`browser_interact`、`browser_wait_for` 七个通用工具；
- `cap-browser-*` 到工具名的运行时映射；
- 独立且稳定的 Agent Target，不随用户切换可见标签页而变化；
- Pause、User Takeover、Activity、稳定错误码和脱敏证据导出。

本批只补齐以下四个产品缺口：

1. 用自动测试证明真实 Agent 实例配置中的 `cap-browser-*` 能变成当轮 LLM 的七个 Tool Definitions；
2. 将 Agent、会话、Run、ToolCall 的非敏感来源信息沿工具调用链传到 Desktop；
3. 让 Agent Browser UI 准确显示“谁正在控制、正在执行什么、何时完成”，并自动维护控制状态；
4. 建立从 Agent Runtime Profile、Tool Execution、认证 Bridge 到 Desktop Handler 的确定性集成测试，然后再执行真实 DeepSeek smoke。

本批完成后，产品链路必须为：

```text
Workspace Agent manifest
  -> AgentRuntimeProfileResolver
  -> LLM Tool Definitions
  -> DeepSeek tool_call
  -> IPuddingToolExecutionService
  -> browser_* tool
  -> RemoteBrowserRuntime
  -> authenticated BrowserBridge
  -> BrowserBridgeCommandDispatcher
  -> BrowserWorkspaceController
  -> WebView2BrowserPage
  -> structured tool result
  -> same Agent reasoning turn
```

## 2. 不得重复实现的内容

不得新建第二套以下组件：

- Browser Runtime、Context、Page、Surface；
- WebView2 DOM Driver；
- WebSocket Bridge、控制令牌或第二个 IPC 通道；
- `browser_*` 同义工具；
- Playwright API 兼容层；
- Douyin 专用选择器、登录、评论读取或回复逻辑；
- 任意 JavaScript 执行工具、Cookie/Storage 导出、CDP 或 Network 拦截。

现有七个工具和通用 Browser Abstractions 是唯一执行入口。本批如发现缺陷，应修补现有路径，不能绕开它另接一条捷径。

## 3. 修改边界

预期允许修改：

```text
Source/PuddingCore/Tools/PuddingToolContracts.cs
Source/PuddingBrowser.Abstractions/BrowserOperationOrigin.cs                  # 新建
Source/PuddingBrowser.Protocol/BrowserBridgeMessages.cs
Source/PuddingBrowser.AgentTools/BrowserAgentToolBase.cs                     # 新建
Source/PuddingBrowser.AgentTools/Browser*Tool.cs
Source/PuddingHost/BrowserBridge/RemoteBrowserRuntime.cs
Source/PuddingHost/BrowserBridge/BrowserBridgeServiceCollectionExtensions.cs
Source/PuddingDesktop/Browser/BrowserBridgeCommandDispatcher.cs
Source/PuddingDesktop/Browser/BrowserWorkspaceController.cs
Source/PuddingDesktop/Views/BrowserWorkspaceView.xaml
Source/PuddingDesktop/Views/BrowserWorkspaceView.xaml.cs
Source/PuddingPlatformTests/Services/AgentRuntimeProfileResolver*Tests.cs
Tests/PuddingBrowser.AgentTools.Tests/**
Tests/PuddingHost.Tests/BrowserBridge/**
Tests/PuddingDesktop.Tests/Browser/**
Docs/07架构/README.md
Docs/README.md
Source/code_map.md
```

如果实际实现不需要修改其中某个文件，不要为了贴合清单制造空改动。若必须扩大到其他生产文件，先在交付总结中说明原因。

开始前执行：

```powershell
git status --short
rg -n "browser_context|cap-browser-context|BrowserBridgeCommand|AgentBrowserActivity" Source Tests
```

只记录并保护现有 dirty worktree，不清理、不回退无关文件。

## 4. 工作包一：冻结 Browser Tool 与 Capability 的真实映射

### 4.1 目标

确保 Agent 实例保存的是 capability ID，而 Agent Runtime 实际下发给 LLM 的是对应工具名，并用自动测试阻止以后发生静默漂移。

当前约定必须保持：

| Capability ID | Tool name |
|---|---|
| `cap-browser-context` | `browser_context` |
| `cap-browser-tabs` | `browser_tabs` |
| `cap-browser-navigate` | `browser_navigate` |
| `cap-browser-snapshot` | `browser_snapshot` |
| `cap-browser-locate` | `browser_locate` |
| `cap-browser-interact` | `browser_interact` |
| `cap-browser-wait-for` | `browser_wait_for` |

### 4.2 新增集中常量

在 `PuddingBrowser.AgentTools` 新建：

```csharp
namespace PuddingBrowser.AgentTools;

public static class BrowserAgentToolIds
{
    public const string Context = "browser_context";
    public const string Tabs = "browser_tabs";
    public const string Navigate = "browser_navigate";
    public const string Snapshot = "browser_snapshot";
    public const string Locate = "browser_locate";
    public const string Interact = "browser_interact";
    public const string WaitFor = "browser_wait_for";

    public static IReadOnlyList<string> All { get; }
}

public static class BrowserAgentCapabilityIds
{
    public const string Context = "cap-browser-context";
    public const string Tabs = "cap-browser-tabs";
    public const string Navigate = "cap-browser-navigate";
    public const string Snapshot = "cap-browser-snapshot";
    public const string Locate = "cap-browser-locate";
    public const string Interact = "cap-browser-interact";
    public const string WaitFor = "cap-browser-wait-for";

    public static IReadOnlyList<string> All { get; }
}
```

要求：

- `All` 顺序固定为 Context、Tabs、Navigate、Snapshot、Locate、Interact、WaitFor；
- 不在 Platform、Host、Desktop 再维护另一份七项硬编码列表；
- `Tool` Attribute 的 `id` 仍是编译期常量，改为引用 `BrowserAgentToolIds.*`；
- 如果项目引用方向导致 Platform 不能引用 AgentTools，不要反向污染分层；Platform 继续从真实 `ToolDescriptor` 派生 capability ID，测试用七项期望值冻结契约。

### 4.3 Runtime Profile 测试

在 `Source/PuddingPlatformTests/Services/` 增加测试，测试名至少包含：

```csharp
AgentWithAllBrowserCapabilities_ResolvesSevenBrowserToolDefinitions()
AgentWithoutBrowserCapabilities_DoesNotResolveBrowserTools()
UnknownBrowserCapability_IsIgnoredWithoutGrantingAnotherTool()
BrowserCapabilityRoundTrip_PreservesSelectedCapabilityIds()
```

第一项必须用真实 `AgentRuntimeProfileResolver` 和包含七个 browser descriptors 的 Tool Catalog，断言：

```csharp
profile.ToolDefinitions!
    .Select(tool => tool.Name)
    .Where(BrowserAgentToolIds.All.Contains)
```

与 `BrowserAgentToolIds.All` 集合完全相等，不能只断言数量。

同时断言 `profile.CapabilityPolicy` 确实允许这七个工具；未知 capability 不得扩大权限。

## 5. 工作包二：为 Browser 命令携带真实 Agent 调用来源

### 5.1 通用来源上下文

在 `PuddingBrowser.Abstractions/BrowserOperationOrigin.cs` 新建：

```csharp
namespace PuddingBrowser.Abstractions;

public sealed record BrowserOperationOrigin
{
    public required string WorkspaceId { get; init; }
    public required string AgentInstanceId { get; init; }
    public required string SessionId { get; init; }
    public string? ConversationId { get; init; }
    public string? RunId { get; init; }
    public string? ToolCallId { get; init; }
    public required string ToolName { get; init; }
}

public interface IBrowserOperationOriginAccessor
{
    BrowserOperationOrigin? Current { get; }
    IDisposable Push(BrowserOperationOrigin origin);
}

public sealed class BrowserOperationOriginAccessor : IBrowserOperationOriginAccessor
{
    public BrowserOperationOrigin? Current { get; }
    public IDisposable Push(BrowserOperationOrigin origin);
}
```

实现要求：

- 使用 `AsyncLocal`，不能用普通可变 singleton 字段；
- 支持嵌套 Push/Dispose，并在 Dispose 后恢复上一层；
- 并发 Agent 调用之间不得串来源；
- 不携带 Prompt、工具参数、DOM、URL、Cookie、Header、Token、API Key；
- `AgentInstanceId` 优先使用 `ConfigurationAgentInstanceId`，为空时使用 `AgentInstanceId`；
- `ConversationId`、`RunId`、`ToolCallId` 从 `ToolExecutionContext.ExecutionIdentity` 取得。

### 5.2 为所有强类型 Tool 增加可选执行作用域 Hook

在 `PuddingToolBase<TArgs>` 增加非破坏性扩展点：

```csharp
protected virtual IDisposable? BeginExecutionScope(ToolExecutionRequest request)
    => null;
```

`ExecuteAsync` 的主体必须在该作用域中执行：

```csharp
public async Task<ToolExecutionResult> ExecuteAsync(
    ToolExecutionRequest request,
    CancellationToken ct = default)
{
    using var scope = BeginExecutionScope(request);
    // 保留现有反序列化、取消和错误语义
}
```

不得改变现有派生 Tool 的函数签名，也不得吞掉 `OperationCanceledException`。

### 5.3 Browser Tool 基类

新建 `PuddingBrowser.AgentTools/BrowserAgentToolBase.cs`：

```csharp
public abstract class BrowserAgentToolBase<TArgs>(
    IBrowserOperationOriginAccessor originAccessor)
    : PuddingToolBase<TArgs>
    where TArgs : class
{
    protected override IDisposable BeginExecutionScope(ToolExecutionRequest request);
}
```

该方法将 `ToolExecutionRequest.Context` 转成 `BrowserOperationOrigin`，`ToolName` 使用 `Descriptor.ToolId`。

七个 Browser Tool 全部改为继承该基类，例如：

```csharp
public sealed class BrowserSnapshotTool(
    IBrowserRuntime runtime,
    IBrowserOperationOriginAccessor originAccessor)
    : BrowserAgentToolBase<BrowserSnapshotArgs>(originAccessor)
```

除继承与依赖注入外，不改动现有参数、返回结构、稳定错误码和浏览器行为。

### 5.4 Bridge 协议来源字段

在 `PuddingBrowser.Protocol/BrowserBridgeMessages.cs` 增加：

```csharp
public sealed record BrowserBridgeCommandOrigin
{
    public required string WorkspaceId { get; init; }
    public required string AgentInstanceId { get; init; }
    public required string SessionId { get; init; }
    public string? ConversationId { get; init; }
    public string? RunId { get; init; }
    public string? ToolCallId { get; init; }
    public required string ToolName { get; init; }
}
```

并在 `BrowserBridgeCommand` 增加向后兼容的可空属性：

```csharp
public BrowserBridgeCommandOrigin? Origin { get; init; }
```

要求：

- 不改变当前 `ProtocolVersion`；该字段可空，旧测试构造的命令仍有效；
- Desktop 不信任显示文本，只接受这些标识字段，不接受任意 HTML/Markdown；
- 每个字符串进入 Activity 前限制长度，建议 128 字符；
- 来源字段只用于 UI、诊断和关联，不参与授权判定；授权仍在 Core 的 Tool Policy/Firewall。

### 5.5 Remote Runtime 注入来源

调整构造函数：

```csharp
public RemoteBrowserRuntime(
    IDesktopBrowserCommandBroker broker,
    IBrowserOperationOriginAccessor originAccessor)
```

在 `ExecuteAsync<T>` 创建 `BrowserBridgeCommand` 时读取一次 `originAccessor.Current`，映射到 `Origin`。

注意：

- 在命令创建时复制不可变快照，不能把 AsyncLocal 对象延迟读取到发送线程；
- 非 Agent 调用允许 `Origin = null`；
- Context/Page proxy 后续调用必须继续读取当前执行作用域，而不是在创建 proxy 时永久缓存第一次 Agent 身份。

在 `BrowserBridgeServiceCollectionExtensions` 注册：

```csharp
services.TryAddSingleton<IBrowserOperationOriginAccessor, BrowserOperationOriginAccessor>();
```

仍然只在 `DesktopChild && BrowserAutomationEnabled` 时注册 Remote Browser 和 Browser Tools。

## 6. 工作包三：Desktop 自动控制状态与 Agent Activity UI

### 6.1 Activity 模型

扩展 `AgentBrowserActivity` 与 `AgentBrowserActivitySnapshot`：

```csharp
public string? AgentInstanceId { get; }
public string? SessionId { get; }
public string? RunId { get; }
public string? ToolCallId { get; }
public string? ToolName { get; }
```

由 `BrowserBridgeCommand.Origin` 填充。`Target` 继续只保存 Context/Page 标识，不能把 URL 或 Arguments 放进去。

新增 Dispatcher 事件：

```csharp
public event EventHandler<AgentBrowserOperationStateChangedEventArgs>?
    OperationStateChanged;

public sealed record AgentBrowserOperationStateSnapshot
{
    public required int ActiveOperationCount { get; init; }
    public BrowserBridgeCommandOrigin? MostRecentOrigin { get; init; }
}
```

触发规则：

- 命令通过 Pause/Takeover/Deadline/Name 校验并加入 `_activeOperations` 后，发布一次；
- 命令在 `finally` 从 `_activeOperations` 删除后再发布一次；
- 并发命令时 `ActiveOperationCount` 必须准确，不能第一条完成就显示 Idle；
- Pause/UserTakeover 拒绝的命令仍产生一条失败 Activity，便于 Agent 和用户理解；
- `ActivityChanged` 和 `OperationStateChanged` 的订阅者异常不能破坏命令执行。

### 6.2 Controller 状态机

扩展 `IBrowserWorkspaceController`：

```csharp
string CurrentAgentSummary { get; }
Task ApplyOperationStateAsync(
    AgentBrowserOperationStateSnapshot snapshot,
    CancellationToken ct);
```

状态优先级必须固定为：

```text
UserTakeover > Paused > AgentControlling > Idle
```

具体规则：

- 用户点击 Takeover 后始终显示 `UserTakeover`，直到 Resume；
- 用户点击 Pause 后显示 `Paused`，直到 Resume；
- 未暂停且未接管时，`ActiveOperationCount > 0` 显示 `AgentControlling`；
- 活动操作归零后显示 `Idle`；
- Handoff 只设置 Agent Target，不应长期把状态伪装成 `AgentControlling`；
- Bridge 断开、Controller Dispose 或 Target 被关闭时清空当前来源摘要；
- 所有 `ObservableCollection` 和属性通知通过现有 UI Dispatcher 更新。

`CurrentAgentSummary` 建议格式：

```text
{AgentInstanceId} · {ToolName}
```

Session/Run 只用于 Tooltip 和诊断，不在主界面展示完整长 ID。

### 6.3 Windows 11 UI

在 Agent Activity Pane 顶部增加一个紧凑状态 Card：

```text
● Agent 正在控制
默认助手 · browser_interact
目标：Browser automation test
```

要求：

- `AgentControlling` 使用 Accent 色和轻量进度动画；
- `Paused` 使用 Warning 色；
- `UserTakeover` 使用用户图标和“你正在控制”；
- `Idle` 使用中性文本“等待 Agent 操作”；
- 每条 Activity 显示 ToolName、命令摘要、Target、时间、成功/失败；
- fill/type 不显示值；Snapshot 不显示 DOM；Navigate 不显示 query/fragment；
- Pause、Takeover、Resume 保留现有按钮与稳定错误语义；
- 不弹出新的 WebView2 Window，本阶段继续使用现有 Agent Browser 双栏布局；
- 不增加地址栏到 Workbench WebView2，浏览器导航只属于 Agent Browser。

在 `BrowserWorkspaceView.xaml.cs` 订阅与解除订阅 `OperationStateChanged`，并调用 Controller 的 `ApplyOperationStateAsync`。Dispose 后不得继续回调已释放的 View。

## 7. 工作包四：确定性端到端测试

### 7.1 Origin AsyncLocal 测试

在 `Tests/PuddingBrowser.AgentTools.Tests` 增加：

```csharp
BrowserTool_PushesAgentOriginDuringRuntimeCall()
BrowserTool_RestoresPreviousOriginAfterSuccess()
BrowserTool_RestoresPreviousOriginAfterFailure()
ConcurrentBrowserToolCalls_DoNotLeakOriginAcrossAgents()
NonBrowserTool_IsUnaffectedByExecutionScopeHook()
```

并发测试至少并行执行两个不同 Agent/Session 的调用，在假的 `IBrowserRuntime` 内读取 accessor，断言身份不串线。

### 7.2 Remote Runtime 与协议测试

在 `Tests/PuddingHost.Tests/BrowserBridge` 增加：

```csharp
RemoteRuntime_CopiesCurrentOriginIntoEveryBridgeCommand()
RemotePage_DoesNotCacheOriginFromCreatingAgent()
RemoteRuntime_AllowsNullOriginForNonAgentCaller()
AgentTool_ThroughAuthenticatedBridge_PreservesOriginAndResult()
```

`RemotePage_DoesNotCacheOriginFromCreatingAgent` 的测试步骤：

1. Agent A 作用域创建或取得 Remote Page；
2. 退出 A 作用域；
3. Agent B 作用域对同一个 Remote Page 调用 Snapshot；
4. Broker 收到的命令来源必须是 B。

### 7.3 Dispatcher 与 Controller 测试

在 `Tests/PuddingDesktop.Tests/Browser` 增加：

```csharp
Dispatcher_ProjectsOriginIntoActivityWithoutArguments()
Dispatcher_TracksConcurrentActiveOperationCount()
PausedCommand_ProducesSanitizedFailureActivity()
UserTakeoverCommand_ProducesSanitizedFailureActivity()
Controller_UsesAutomaticControlStatePriority()
Controller_HandoffSetsTargetButRemainsIdle()
Controller_ClearsAgentSummaryWhenTargetCloses()
ViewDispose_UnsubscribesOperationStateEvents()
```

必须额外扫描 Activity/Evidence 序列化结果，断言不含以下测试哨兵：

```text
SECRET_FILL_VALUE
Authorization
Cookie
api-key
access_token
```

### 7.4 Runtime Profile 到 Bridge 的组合测试

新增一个不调用真实模型的组合测试，使用：

- 真实 Agent Instance manifest；
- 真实 `AgentRuntimeProfileResolver`；
- 真实 Browser Tool Registry；
- 真实 `IPuddingToolExecutionService` 权限路径；
- 真实 `DesktopBrowserCommandBroker`；
- 测试 WebSocket Desktop 连接或现有 `BrowserBridgeTestHost`；
- 假的 Desktop Handler 返回确定性页面结果。

测试名：

```csharp
ConfiguredAgent_BrowserSnapshotTool_TraversesPolicyExecutionAndAuthenticatedBridge()
```

强制断言：

1. Runtime Profile 包含 `browser_snapshot`；
2. Tool Policy 允许调用；
3. Broker 收到命令；
4. 命令来源包含正确 Agent/Session/Run/ToolCall；
5. Desktop 返回的 PageVersion 和结构化结果回到 Tool；
6. Activity 中没有 Arguments 或 DOM 原文。

禁止用 `registry.GetTool(...).ExecuteAsync(...)` 直接调用来冒充这一条组合测试；必须经过生产 `IPuddingToolExecutionService` 权限入口。

## 8. 工作包五：真实 DeepSeek 功能验收入口

自动测试全部通过后，不再新增生产代码，按文档 77 执行真实模型 smoke。

给产品内测试 Agent 的唯一提示仍为：

```text
请在当前分配给你的 Agent Browser 页面中完成资料表单：姓名填写“Pudding Browser Smoke”，角色选择“Designer”，勾选接受条款并保存。请在页面明确出现“Saved”之后告诉我结果；请自行决定如何完成，不要让我手动点击。
```

通过时必须观察到：

- UI 显示实际 Agent ID 和当前 browser tool；
- `snapshot -> locate -> interact -> wait_for` 的合理工具序列；
- 同一个 Context/Page 身份被复用；
- 页面真实出现 `Saved`；
- Agent 在结果出现后才回复完成；
- Activity 和导出证据中没有合成表单值或 Secret。

## 9. 编译与测试命令

按以下顺序串行执行，避免运行中 Desktop/Core 锁定输出：

```powershell
dotnet build Source\PuddingBrowser.Abstractions\PuddingBrowser.Abstractions.csproj --no-restore --nologo
dotnet build Source\PuddingBrowser.Protocol\PuddingBrowser.Protocol.csproj --no-restore --nologo
dotnet build Source\PuddingBrowser.AgentTools\PuddingBrowser.AgentTools.csproj --no-restore --nologo

dotnet test Tests\PuddingBrowser.AgentTools.Tests\PuddingBrowser.AgentTools.Tests.csproj --no-restore --nologo
dotnet test Source\PuddingPlatformTests\PuddingPlatformTests.csproj --no-restore --nologo
dotnet test Tests\PuddingHost.Tests\PuddingHost.Tests.csproj --no-restore --nologo
dotnet test Tests\PuddingDesktop.Tests\PuddingDesktop.Tests.csproj --no-restore --nologo

powershell -ExecutionPolicy Bypass -File TestScripts\start-phase2a3-webview2-smoke.ps1 -HoldSeconds 0
dotnet build Source\PuddingDesktop\PuddingDesktop.csproj --no-restore --nologo
```

如果完整测试项目存在仓库既有失败，必须同时给出：

- 本批新增测试结果；
- 失败测试名；
- 是否能在未修改基线复现；
- 为什么不影响或为什么阻断本批。

不得通过跳过测试、放宽断言、吞异常或删除测试来获得通过。

## 10. 完成定义

以下条件必须全部满足：

- [ ] 七个 capability ID 与七个 Tool Definition 的真实映射有自动测试；
- [ ] 未授权 Agent 看不到 Browser Tools，未知 capability 不扩大权限；
- [ ] Browser Tool 调用来源通过 AsyncLocal 正确隔离；
- [ ] Bridge Command 携带 Agent/Session/Run/ToolCall 的非敏感来源；
- [ ] Remote Page 不缓存创建者身份；
- [ ] Desktop Activity 显示实际 Agent 和 Tool；
- [ ] 控制状态由活动命令自动切换，Handoff 不再长期显示“正在控制”；
- [ ] Pause/UserTakeover 优先级和稳定错误码不回归；
- [ ] Activity/Evidence 不包含参数值、DOM、Cookie、Token 或 Secret；
- [ ] 组合测试经过 `IPuddingToolExecutionService` 和认证 Bridge；
- [ ] AgentTools、Platform、Host、Desktop 测试通过；
- [ ] 真实 WebView2 smoke 不回归；
- [ ] `Docs/07架构/README.md`、`Docs/README.md`、`Source/code_map.md` 已更新。

## 11. 开发 Agent 交付格式

完成后只按以下结构报告：

```markdown
## Phase 2A-3C 交付

### 变更
- 新建：...
- 修改：...

### 闭环证据
- Agent capability -> ToolDefinitions：...
- ToolExecutionService -> Bridge：...
- Bridge -> Desktop Activity：...
- Desktop -> WebView2 -> Tool result：...

### 测试
- 命令：...
- 结果：通过数/失败数

### 未完成
- 无 / 明确列出阻断项

### 状态
ready-for-external-deploy
```

状态只能是 `ready-for-external-deploy`，不要提前宣布真实 DeepSeek 和 Desktop 生命周期最终验收通过。

