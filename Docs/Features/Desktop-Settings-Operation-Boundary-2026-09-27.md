# DS-00 设置接入基线与生命周期（一页接入说明）

日期：2026-09-27。范围：`Docs/Tasks/Desktop-Admin-Settings-DeepSeek-2026-09-27.md` 的 DS-00。本文是后续 DS-01…DS-17 的唯一接入方式说明。

## 1. 层次与依赖方向

```
WinUI Shell (Source/PuddingDesktop)
   │  只引用 Foundation / Composition / PuddingChat*
   ▼
Foundation (BCL 叶组件)        契约：IDesktopKernel、ISettingsScope、ISettingsOperationHost、SettingsOperationGate
   ▲
   │  实现
Composition (PuddingDesktop.Composition)  DesktopKernelFactory.Session → 每操作一个 DI 作用域
   ▼
Core（进程内 PuddingHost / PuddingPlatform 应用服务、DbContext、文件配置）
```

- Shell 不引用业务工程；`PuddingDesktop.csproj` 的 `EnforceShellBoundary` 编译期门禁保持有效。
- **不新增 HTTP 客户端、REST 适配层或逐接口转发方法**。设置页把「要做什么」写成委托，Composition 在真实 Core 作用域里调用既有业务服务。
- 唯一的请求级 API：

```csharp
var stamp = _kernel.Settings.Capture();                       // 目标上下文（内核代次 + 所选工作区/角色）
var value = await _kernel.RunSettingsAsync("llm.providers.read", async (scope, ct) =>
{
    var store = scope.Services.GetRequiredService<LlmProviderFileService>();   // 只出现在 Composition 侧适配器里
    return await store.ListProvidersAsync(ct);
});
_kernel.Settings.EnsureCurrent(stamp);                        // 迟到结果在此作废，不写入新页面
```

`scope.Services` 是 `IServiceProvider`（BCL 类型），Foundation 不引入 DI/Host/EF。

## 2. 已实现并由测试固定的规则

| # | 规则 | 行为 | 固定它的测试 |
|---|---|---|---|
| 1 | Host 未就绪 | 拒绝执行并给出可显示原因（NotConfigured/Stopped/Starting/Stopping/Failed），**不返回假成功** | `SettingsOperationTests.KernelIsNotReady_OperationIsRefusedWithReason`、`KernelWithoutSettingsHost_ReportsUnavailableInsteadOfFakingSuccess`；窗口 smoke「settings operation refused without Core」 |
| 2 | 停止中拒绝新操作 | `Stopping` 起新操作立即抛 `SettingsUnavailableException(KernelStopping)` | `Stopping_RefusesNewWorkAndDrainsAcceptedWork`、`DesktopCompositionTests.SettingsOperationsRunInsideRealHost_AndStopInvalidatesThem` |
| 3 | 取消与排空 | 已接受操作先随调用方令牌取消，再由 `InProcessKernel` 排空，之后才停止/释放会话作用域 | `AcceptedWorkObservesCallerCancellation`、`KernelDrainsAcceptedSettingsBeforeReleasingSession` |
| 4 | 实例选择代次 | 新内核代次或工作区/角色切换都会作废旧 `SettingsStamp`；`EnsureCurrent` 抛 `SettingsSupersededException` | `NewKernelGenerationInvalidatesEarlierStampAndClearsSelection`、`SelectionChangeInvalidatesEarlierStamp_UnchangedSelectionKeepsIt`；窗口 smoke「Agent switch invalidates earlier settings stamps」 |
| 5 | 版本冲突 | 带版本的写入用 `SettingsVersionGuard.EnsureCurrent` 比较，过期版本抛 `SettingsConflictException` 并要求重新读取 | `VersionConflictIsReportedAndUnversionedWritesPassThrough` |

补充：无 Core 时 17 个分类、49 个页签仍全部可浏览（目录是静态展示清单，不触发服务调用）；`Core 状态`一律读 `IDesktopKernel.Snapshot`，不使用常量“就绪”。

## 3. 本机管理身份结论（DS-00 裁定）

- 原生客户端**不新增登录页，也不新造主体**。设置操作以本机既有单用户身份运行：`LocalDesktopIdentity.UserId == "single-user"`，与原生聊天一致，是编译期常量，**不来自输入框、命令行或 HTTP 入口**；`Owns` 只做序号相等匹配，`"Single-User"`、带空格或 `"admin"` 都不算本机所有者。
- 该身份**不等于 Web Admin 角色**。工作区授权、AccessToken Owner/scope、审批审计的“批准人/来源”等由 Core 既有业务规则判定；Desktop 只传递 Core 契约要求的真实身份，不得靠任意构造用户名跳过规则。
- 远程认证保持独立：不新增匿名准入，也不为内嵌调用新增 HTTP 接口。`/api/desktop/*` 仍需 admin JWT（401 无令牌），`DesktopCompositionTests.RealHostStartsInProcess_...` 继续固定这一点。

## 4. 新增一个设置切片的步骤

1. **核对源字段与 Core 方法**：对照任务书第 7 节与源证据附录；缺口（如 LLM 配额）先补 Core 并独立测试，不允许伪造保存成功。
2. **下沉共享业务操作**：若逻辑只在 Controller 内且有实际业务职责，先下沉为可独立测试的 Core 应用操作，Web 与 Desktop 共用；不要伪造 `HttpContext`。
3. **在 Composition 绑定**：新增窄适配器，只在该层出现业务类型；通过 `ISettingsOperationHost`/`ISettingsScope` 取服务，不新增通用 `SettingsClient`。
4. **页面接入**：读 `Capture()` → `RunSettingsAsync` → `EnsureCurrent()`；加载/空数据/未就绪/失败重试/无权限都要有真实状态；写操作显示脏表单、校验、保存中、防重复提交，失败保留草稿，版本冲突重新读取。
5. **独立验证 → 集成验证 → 精确提交 → 更新 catalog 状态**：一个切片一个 commit，只暂存本次路径。

## 5. 复现命令

```powershell
dotnet test Source/PuddingDesktop.FoundationTests/PuddingDesktop.FoundationTests.csproj `
  --artifacts-path temp/build/desktop-kernel --results-directory temp/test-out/ds00-foundation `
  -p:CoverletOutput=E:/github/AgentNetworkPlan/PuddingAgent/temp/test-out/ds00-foundation/coverage
dotnet test Source/PuddingDesktop.CompositionTests/PuddingDesktop.CompositionTests.csproj `
  --artifacts-path temp/build/desktop-kernel --results-directory temp/test-out/ds00-composition
pwsh -NoProfile -File TestScripts/test-pudding-winui-skeleton.ps1 -TimeoutSeconds 300
```

绝对路径写覆盖率输出，避免 `TestResults/` 落进源码目录；隔离 DataRoot，不使用 `D:\data`，不复制真实凭据。

## 6. 交付层次（不混淆证据强度）

| 层次 | 本次结论 |
|---|---|
| 源码 / 构建测试 | Foundation 36 项、Composition 2 项通过；`PuddingDesktop.csproj` Release 0 错误 |
| 隔离窗口 smoke | `TestScripts/test-pudding-winui-skeleton.ps1` 真实 WinUI 进程、独立 `--state-root`、不启动 Core，**93 项检查通过**（含 6 项 DS-00 新增） |
| 实际产品部署 | **未做**。没有重启用户当前运行的 Desktop，不能宣称运行中的产品已加载本次代码 |
