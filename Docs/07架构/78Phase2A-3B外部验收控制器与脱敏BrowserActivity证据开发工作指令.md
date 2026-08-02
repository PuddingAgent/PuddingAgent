# Phase 2A-3B：外部验收控制器与脱敏 Browser Activity 证据开发工作指令

> 执行者：运行在当前 Pudding 内的开发 Agent  
> 验收者：Pudding 进程外的 Codex/人工控制器  
> 开发结束状态：`ready-for-external-deploy`，不得自行宣布产品验收通过  
> 依赖：[77Phase2A-3B真实DeepSeekAgent浏览器工具选择验收工作指令](77Phase2A-3B真实DeepSeekAgent浏览器工具选择验收工作指令.md)

## 1. 目标

补齐两个可开发、可单测、但必须由外部进程最终验收的能力：

1. 为 Agent Browser 增加“导出脱敏活动记录”，输出命令名、页面身份、结果和稳定错误码，不输出工具参数或页面数据；
2. 增加外部 Desktop smoke 控制脚本，负责确认没有旧实例、启动明确发布目录的新 Desktop、托管 TestSite、记录 PID/路径，并在 Desktop 退出后检查其拥有的子进程是否残留。

完成后，内部开发 Agent 只提交构建、单测、静态检查和 `ready-for-external-deploy` 交接。外部验收者会重启 Pudding，再让产品内测试 Agent执行 77 的真实 DeepSeek Browser Tools 任务。

## 2. 不在本批实现

- 不实现 Douyin Adapter、页面选择器或评论业务。
- 不增加 Browser Tool，不修改 Snapshot/Locator/Interact/Wait 语义。
- 不新增读取 Cookie、Token、Storage、LLM provider 配置或 Secret 的代码。
- 不自动登录 Workbench，不在脚本中保存用户名、密码或 JWT。
- 不通过脚本自动发送 Agent prompt；真实模型任务由外部验收者在新会话中发出。
- 不增加生产环境专用的“测试后门”HTTP API。
- 不让内部开发 Agent停止、重启或替换承载自己的 PuddingDesktop/Core。

## 3. Changed-file 边界

计划新增：

```text
Source/PuddingDesktop/Browser/BrowserActivityEvidenceExporter.cs
Tests/PuddingDesktop.Tests/Browser/BrowserActivityEvidenceExporterTests.cs
TestScripts/start-phase2a3b-external-acceptance.ps1
```

计划修改：

```text
Source/PuddingDesktop/Browser/BrowserWorkspaceController.cs
Source/PuddingDesktop/Views/BrowserWorkspaceView.xaml
Source/PuddingDesktop/Views/BrowserWorkspaceView.xaml.cs
Agents.md
How-Debuge.md
Source/code_map.md
Docs/07架构/77Phase2A-3B真实DeepSeekAgent浏览器工具选择验收工作指令.md
Docs/07架构/78Phase2A-3B外部验收控制器与脱敏BrowserActivity证据开发工作指令.md
```

若必须超出边界，先在汇报中说明原因；不得顺手整理其他 Phase 2 文件。

## 4. 脱敏证据模型

在 `PuddingDesktop.Browser` 新增：

```csharp
public sealed record BrowserActivityEvidenceDocument
{
    public int SchemaVersion { get; init; } = 1;
    public required DateTimeOffset CapturedAt { get; init; }
    public required string BridgeState { get; init; }
    public required string ControlState { get; init; }
    public string? ActiveContextId { get; init; }
    public string? ActivePageId { get; init; }
    public string? AgentTargetPageId { get; init; }
    public required IReadOnlyList<BrowserActivityEvidenceItem> Activities { get; init; }
}

public sealed record BrowserActivityEvidenceItem
{
    public required Guid OperationId { get; init; }
    public required string CommandName { get; init; }
    public required string Target { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public bool? Success { get; init; }
    public string? ErrorCode { get; init; }
}

public interface IBrowserActivityEvidenceExporter
{
    Task<string> ExportAsync(
        BrowserActivityEvidenceDocument document,
        string destinationDirectory,
        CancellationToken cancellationToken);
}

public sealed class BrowserActivityEvidenceExporter : IBrowserActivityEvidenceExporter
{
    public Task<string> ExportAsync(
        BrowserActivityEvidenceDocument document,
        string destinationDirectory,
        CancellationToken cancellationToken);
}
```

实现要求：

- 使用 `System.Text.Json`，camelCase，UTF-8；
- 先写同目录临时文件，再原子替换/移动为 `browser-activity-<UTC timestamp>.sanitized.json`；
- destination 必须由调用方显式传入，Exporter 不读取仓库路径；
- 不接受或序列化 `BrowserBridgeCommand.PayloadJson`、fill/type value、DOM Snapshot、URL query、Cookie、Header 或 Token；
- `Target` 只能保留现有 dispatcher 生成的 context/page ID；如果不是稳定 ID，输出 `"-"`；
- 导出失败不能清空 Activity，也不能影响 Browser/Bridge 运行。

## 5. Controller 快照

在 `IBrowserWorkspaceController` / `BrowserWorkspaceController` 增加：

```csharp
BrowserActivityEvidenceDocument CaptureActivityEvidence(DateTimeOffset capturedAt);
```

规则：

- 必须在 UI Dispatcher 上获得一致快照；若接口必须异步，可改为：

```csharp
Task<BrowserActivityEvidenceDocument> CaptureActivityEvidenceAsync(
    DateTimeOffset capturedAt,
    CancellationToken cancellationToken);
```

- 只复制 `Activities` 当前最多 100 条的安全字段；
- 按 `StartedAt` 升序输出，保证真实调用顺序；
- BridgeState、ControlState、Active/Target 身份来自 Controller 当前事实源；
- 不能从工具结果或协议 Payload 补充“更详细”字段。

优先选择异步签名，避免调用方错误地跨线程读取 `ObservableCollection`。

## 6. Windows 11 UI

在 Agent Activity Pane 标题区增加一个紧凑按钮：

```text
导出记录
```

行为：

1. 若 Browser 未初始化，按钮禁用；
2. 点击后调用 Controller 快照和 Exporter；
3. 默认目录为 `<DataRoot>/diagnostics/browser-activity/`；
4. 成功后在底部状态栏显示文件绝对路径，并用 Explorer 选中文件；
5. 失败时只显示短错误，不把异常堆栈或数据写入 UI；
6. 不弹出保存对话框，不阻塞 WebView2 UI 线程；
7. 使用现有 `CompactButtonStyle` / Windows 11 主题资源，不增加新 UI 框架。

在 `BrowserWorkspaceView.xaml.cs` 增加：

```csharp
private async void ExportActivity_Click(object sender, RoutedEventArgs e);
```

若需要便于测试，把路径选择和 Explorer 打开拆成小函数，不在 event handler 中混入 JSON 构造。

## 7. 外部验收控制脚本

新增：

```text
TestScripts/start-phase2a3b-external-acceptance.ps1
```

参数：

```powershell
param(
    [Parameter(Mandatory = $true)]
    [string]$PublishRoot,

    [Parameter(Mandatory = $true)]
    [string]$DataRoot,

    [int]$StartupTimeoutSeconds = 120,

    [switch]$PrepareOnly,

    [switch]$KeepArtifacts
)
```

### 7.1 可复用函数

脚本至少拆出：

```powershell
function Resolve-PublishLayout { param([string]$PublishRoot) }
function Assert-NoPuddingDesktopInstance { }
function New-AcceptanceWorkspace { param([string]$DataRoot) }
function Start-BrowserTestSite { param([string]$EvidenceRoot) }
function Start-TargetDesktop { param($Layout, $Workspace) }
function Wait-DesktopWindow { param($Process, [int]$TimeoutSeconds) }
function Find-OwnedCoreProcess { param([int]$DesktopPid) }
function Get-OwnedProcessTree { param([int]$RootPid) }
function Write-SanitizedJson { param([string]$Path, $Value) }
function Wait-OwnedChildrenExit { param([int[]]$ProcessIds, [int]$TimeoutSeconds) }
```

可以调整名称，但必须保持解析、启动、观察、序列化分层。

### 7.2 启动前验证

验证以下文件：

```text
<PublishRoot>/PuddingDesktop.exe
<PublishRoot>/core/PuddingAgent.exe
<PublishRoot>/core/wwwroot/admin/index.html
<PublishRoot>/core/PuddingBrowser.AgentTools.dll
```

并执行：

- 使用 `Get-CimInstance Win32_Process` 检查任何现有 `PuddingDesktop.exe`；存在即失败并打印 PID/ExecutablePath，不自动结束；
- `DataRoot` 必须已经存在，不创建副本，不读取 provider 配置文件；
- 创建 `%TEMP%/PuddingAgent/phase2a3b-external-<guid>/desktop-home`；
- 临时 `desktop.json` 只记录显式 DataRoot、发布 Core 路径、窗口尺寸和 `ExitAndStopCore`；
- 不写入或 Patch `<DataRoot>/config/system.json`；
- 证据目录为仓库 `.tmp-test-out/phase2a3b-deepseek-smoke/<UTC timestamp>/`。

### 7.3 `-PrepareOnly`

此模式供 Pudding 内部开发 Agent 验证：

- 参数和发布布局；
- 临时目录边界；
- desktop.json 内容不含 Secret；
- 输出 `internal-handoff.json`，状态为 `ready-for-external-deploy`；
- 不启动 Desktop、Core、TestSite 或真实模型调用。

### 7.4 正式模式

只由外部验收者执行：

1. 启动动态 Loopback TestSite并解析 URL；
2. 使用 `ProcessStartInfo` 启动明确的 Desktop 路径，设置隔离 `PUDDING_DESKTOP_HOME`；
3. 记录 Desktop PID、可执行路径、MainWindowHandle、TestSite PID/URL 和启动前 WebView2 PID 集合；
4. 发现 Desktop 的直接 Core 子进程后记录 PID/ExecutablePath；
5. 输出 `external-controller-ready` JSON，提示验收者进入目标 Desktop 执行 77；
6. 脚本持续等待，不自动发送 prompt，不点击 UI，不自动关闭 Desktop；
7. Desktop 明确退出后，按启动时记录的 PID/父子关系检查 Core/WebView2 残留；
8. 写入 `shutdown-observation.json`；
9. finally 只停止本脚本创建的 TestSite，不结束其他 Pudding/WebView2 进程。

禁止使用 `Get-Process msedgewebview2 | Stop-Process` 之类的全局清理。

## 8. 自动测试

在 `BrowserActivityEvidenceExporterTests` 至少覆盖：

1. JSON 只包含允许字段；
2. 活动按开始时间升序；
3. success/errorCode 原样保留；
4. 输出文件名稳定且扩展名为 `.sanitized.json`；
5. 取消传播且不留下最终半文件；
6. 原子写失败不覆盖已有文件；
7. 输入对象即使在测试中包含敏感哨兵，也不会出现在输出（哨兵只能放在不被模型接受的测试外围对象，不能扩展生产 DTO）；
8. Controller 快照在 UI Dispatcher 上执行；
9. Active/Target Page 和 Bridge/Control 状态正确；
10. 超过 100 条时仍只导出 Controller 已保留的 100 条。

为脚本增加 `-PrepareOnly` 验证场景：使用仓库临时 publish 目录和系统 Temp，不启动 Desktop；断言 `internal-handoff.json` 存在且不包含 `controlToken`、`authorization`、`cookie`、`apiKey`、`secret`（大小写不敏感）。

## 9. 内部开发 Agent 验证命令

串行执行：

```powershell
dotnet build Source\PuddingDesktop\PuddingDesktop.csproj --no-restore --nologo
dotnet test Tests\PuddingDesktop.Tests\PuddingDesktop.Tests.csproj --no-restore --nologo

dotnet publish Source\PuddingDesktop\PuddingDesktop.csproj `
  -c Release --no-restore `
  -o .tmp-build\phase2a3b-external-preview --nologo

powershell -ExecutionPolicy Bypass `
  -File TestScripts\start-phase2a3b-external-acceptance.ps1 `
  -PublishRoot .tmp-build\phase2a3b-external-preview `
  -DataRoot D:\data `
  -PrepareOnly

git diff --check
```

内部 Agent 不得去掉 `-PrepareOnly`，不得为了完成任务停止当前 Desktop/Core，也不得声称正式脚本已通过真实生命周期 smoke。

## 10. 内部交接格式

最终只允许报告：

```text
Phase 2A-3B development status: ready-for-external-deploy | blocked
Build: <result>
Desktop tests: <passed>/<total>
PrepareOnly: <result + handoff path>
Publish layout: <absolute path>
Activity exporter safety tests: <result>
Current Pudding restarted by agent: false
Real DeepSeek smoke claimed by agent: false
Files changed: <exact list>
Known warnings: <pre-existing/new>
External acceptance command: <exact command without secrets>
```

## 11. Definition of Done

- [ ] Exporter DTO 不存在 payload/value/header/cookie/token 字段。
- [ ] Controller 提供 UI 线程一致的脱敏 Activity 快照。
- [ ] Agent Browser 可以导出 Windows 11 风格活动证据。
- [ ] 输出采用原子写且失败不影响 Browser。
- [ ] 外部脚本拒绝已有 Desktop，不擅自停止进程。
- [ ] `PrepareOnly` 不启动任何产品/测试进程。
- [ ] 正式脚本只拥有自己创建的 Desktop/TestSite，并按 PID 观察退出。
- [ ] DataRoot 不被复制、重置或用于存放 build/test 输出。
- [ ] Desktop 定向构建、测试、Release publish、PrepareOnly 和 `git diff --check` 通过。
- [ ] 内部 Agent只交付 `ready-for-external-deploy`，最终验收明确留给外部控制器。

