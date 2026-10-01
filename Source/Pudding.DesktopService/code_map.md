# Pudding.DesktopService CodeMAP

> Desktop 侧能力服务（计划 §6）：**目标校验 · 准入 · 生命周期 · 取消与关闭竞态 · UI 线程边界**
> 依赖：`Pudding.Contracts`（平台无关契约）+ `Pudding.DesktopConnection`（执行器接缝）。
> 编译期 Target `EnforceDesktopServiceBoundary` 禁止引用 Host/Runtime/Desktop/Browser 工程与 ASP.NET Core/WinUI/WebView2 包。
> 测试：`Source/Pudding.DesktopServiceTests`（**58 用例**；假 UI 调度器确定性验证，不需要 WinUI 应用）

## 职责边界

| 属于本组件 | 不属于本组件 |
|---|---|
| 目标存在性/可信级别/页面版本校验 | 访问 WebView2、窗口、通知（`IDesktopUiSurface` 实现方） |
| 能力启用（`AllowedCapabilities`）与策略判定 | DispatcherQueue 的持有与调度（`IDesktopUiDispatcher` 实现方） |
| 暂停/用户接管语义 | 网络读写、序列化、重连（Pudding.DesktopConnection） |
| 入队后竞态复检、取消与期限映射 | Core 侧授权/准入（Core Tool Runtime） |
| UI 调度的异常与拒绝折叠为领域错误 | 审计（连接组件按操作记录） |

## 文件

| 文件 | 用途 |
|---|---|
| `DesktopService.cs` | 管线：`ValidateAdmission`（入队前 + 拿到 UI 线程后**各一次**）→ `IDesktopUiDispatcher.InvokeAsync` → `ExecuteOnUiAsync`（deadline/取消/surface 异常映射）。实现 `IDesktopCapabilityExecutor`；另暴露只读直连 API `GetPageStateAsync`（同样过校验与调度） |
| `DesktopTargetRegistry.cs` | 页面/上下文登记表（唯一真源）：`RegisterContext/RegisterPage/UpdatePage/ClosePage/CloseContext/Resolve`。版本**只允许前进**（回退会让旧 Snapshot/Locator 重新有效，必须拒绝） |
| `DesktopCapabilityPolicy.cs` | 能力 × 可信级别准入表（8 能力 × 3 级别，快照断言）：脚本注入仅 `AgentAuthorized`；对话框/Picker/剪贴板仅 `Workbench`；`Workbench` **永不**允许脚本注入 |
| `DesktopInteractionState.cs` | 暂停与用户接管两个**独立轴**：变更类能力被拒（`Paused`/`UserTakeover`，接管优先），只读能力仍可用（观测不打断用户） |
| `DesktopServiceOptions.cs` | `AllowedCapabilities`、`ShellCallerTrust`（默认 `Untrusted` ⇒ 对话框/Picker/剪贴板默认不开放） |

## 错误映射（终态语义）

| 情形 | 结果 |
|---|---|
| 目标未登记/已关闭 | `invalid_target`（不触碰 UI） |
| 可信级别不足（含工作台脚本注入） | `unauthorized` |
| 期望页面版本不符 | `page_version_mismatch`（入队前与入队后各判一次） |
| 暂停 / 用户接管 | `paused` / `user_takeover`（接管优先） |
| 窗口关闭（含排队中关闭） | `ui_unavailable`，**排队中的调用必须结束而不是悬挂** |
| DispatcherQueue 拒绝入队 / 调度器已释放 | `ui_unavailable` |
| 未启用或无命令 payload 的能力 | `unsupported_capability`（不假装执行） |
| 期限已过（排队期间过期 / surface 调用中过期） | `deadline_exceeded`；从**未进入 surface** 时 `mayHaveSideEffects=false`，进入后按能力 Traits 标注 |
| 取消（入队前 / 排队中 / surface 调用中） | `cancelled`，副作用标注同上 |
| surface 抛异常 | `internal_error`，只保留异常类型，不透出异常消息（可能含页面数据） |

## 有意取舍（诚实登记）

- **交互类能力（对话框/Picker）的单窗口互斥随切片 E 落地**：本切片这两个能力还没有命令 payload，
  实现互斥门会得到无法端到端验证的代码（违反「能编译 ≠ 已测试」），故先不做。
- **`webview.page_state` 没有 wire 命令 payload**：作为只读直连 API 提供（`GetPageStateAsync`），
  UI 侧门面直接调用；补 payload 属于切片 D。
- **WinUI `DispatcherQueue` 适配器未在本切片交付**：它是 20 行平台包装、无独立可测逻辑，
  随「装配宿主」（切片 C-2/D）一起落地，并在 WinUI 应用内做线程访问定向验证。

## 门禁（2026-10-01 实测）

- 独立构建：`dotnet build Source\Pudding.DesktopService -c Release` ⇒ 0 警告 / 0 错误。
- 独立测试：`Pudding.DesktopServiceTests` ⇒ **58/58 通过**（含 5 条边界断言与策略表快照）。
- 边界强制：csproj 只允许 `Pudding.Contracts` + `Pudding.DesktopConnection`、零包引用，由 Target 取红。
