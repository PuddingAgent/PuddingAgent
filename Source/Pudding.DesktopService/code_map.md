# Pudding.DesktopService CodeMAP

> Desktop 侧能力服务（计划 §6）：**目标校验 · 准入 · 生命周期 · 取消与关闭竞态 · UI 线程边界**
> 依赖：`Pudding.Contracts`（平台无关契约）+ `Pudding.DesktopConnection`（执行器接缝）。
> 编译期 Target `EnforceDesktopServiceBoundary` 禁止引用 Host/Runtime/Desktop/Browser 工程与 ASP.NET Core/WinUI/WebView2 包。
> 测试：`Source/Pudding.DesktopServiceTests`（**81 用例**；假 UI 调度器与假监督器确定性验证，不需要 WinUI 应用）
> 历史变更与门禁记录已迁至 [`Docs/00_changelog/2026Year/10/2026-10-02-Pudding.DesktopService-code_map迁出的变更记录.md`](../../Docs/00_changelog/2026Year/10/2026-10-02-Pudding.DesktopService-code_map迁出的变更记录.md)。本文件只保留索引，不再追加日志。

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
| `DesktopService.cs` | 管线：`ValidateAdmission`（入队前 + 拿到 UI 线程后**各一次**）→ `IDesktopUiDispatcher.InvokeAsync` → `ExecuteOnUiAsync`（deadline/取消/surface 异常映射）。实现 `IDesktopCapabilityExecutor`；另暴露只读直连 API `GetPageStateAsync`（同样过校验与调度）。**唯一出口回写观察到的页面版本**（`ExecuteAsync` 拆出 `ExecuteCoreAsync`，成功结果里的版本写入 `DesktopTargetRegistry`）——准则是「谁告诉 Core 版本，谁就是准入的比较基准」，漏了这一步则第一个带版本的操作就被判 `page_version_mismatch` |
| `DesktopTargetRegistry.cs` | 页面/上下文登记表（唯一真源）：`RegisterContext/RegisterPage/UpdatePage/ClosePage/CloseContext/Resolve`。版本**只允许前进**（回退会让旧 Snapshot/Locator 重新有效，必须拒绝） |
| `DesktopCapabilityPolicy.cs` | 能力 × 可信级别准入表（8 能力 × 3 级别，快照断言）：脚本注入仅 `AgentAuthorized`；对话框/Picker/剪贴板仅 `Workbench`；`Workbench` **永不**允许脚本注入 |
| `DesktopInteractionState.cs` | 暂停与用户接管两个**独立轴**：变更类能力被拒（`Paused`/`UserTakeover`，接管优先），只读能力仍可用（观测不打断用户） |
| `DesktopServiceOptions.cs` | `AllowedCapabilities`、`ShellCallerTrust`（默认 `Untrusted` ⇒ 对话框/Picker/剪贴板默认不开放） |
| `DesktopCapabilityHost.cs` | 宿主组合（切片 C-2）：只依赖 `IDesktopConnectionSupervisor` 端口 + `DesktopService`；启动时**二选一传输**（选旧 Bridge 则拒绝启动，不做跨传输回退）；同一 DesktopId **只允许一个活动传输**（进程级占用，停止即释放）；`StartAsync`/`StopAsync`/`WaitForStateAsync`；停止超时仍释放占用 |
| `DesktopChannelTransportResolver.cs` | 把 Core 发布的端点描述解析成 Desktop 传输（计划 §7）：版本不支持 / 形态未知 / 地址不可用一律明确失败；**凭据不从描述里取**（描述没有凭据字段，凭据只能由主机侧注入） |
| `DesktopCapabilityChannelSettings.cs` | `desktop.json` 对应段的**值对象**（不读配置文件，由宿主映射进来）：`Enabled` 缺省 false（继续走旧 Bridge）、`SectionName = "Desktop:CapabilityChannel"`、`DeclaredCapabilities` 是**代码事实**（实现即声明，不从配置放宽）、`CreateConnectionOptions` / `ResolveTransportFromDescription` |
| `DesktopCapabilityChannelPreflight.cs` | 「要不要启动能力通道」的**单一判定入口**：未启用 ⇒ 不启动；启用但端点描述缺失/不可解析 ⇒ **不启动且不回退**；否则可启动 |
| `DesktopCapabilityHostFactory.cs` | 用配置构造宿主：关闭 ⇒ 明确失败**且不构造**；启用 ⇒ 只构造**不启动**（启动名额留给组合根，见规格 §7.2 的陷阱） |
| `DesktopCapabilityChannelComposition.cs` | **组合根**（唯一会改变产品行为的一步）：判定 → 构造 → `StartAsync` → `DisposeAsync` 一次调用完成。关闭 ⇒ `Success(null)` 什么都不做；启用却起不来 ⇒ **明确失败**（不是只记日志）；不做跨传输回退；缺省服务策略 fail-closed |
| `DesktopShellSurface.cs` | 把两个 Shell 端口（交互设施 + 宿主告知）适配成表面：预算纵深防御（剪贴板二次截断）、异常折叠为 `internal_error`、取消原样传播；**不补齐**自动化状态与页面数（那是 `DesktopService` 的权威状态） |
| `BrowserTargetRegistry.cs` | 浏览器目标注册表（`IDesktopBrowserTargetRegistry` 实现）：上下文可信级别 / Agent 目标 / 活动页。`SetAgentTarget` 是**撤销的**一等操作（`RegisterPage(isAgentTarget:false)` 只增不减，撤销缺失会让注册表替不该被驱动的页面背书） |
| `DesktopMutationInvariants.cs` | 变更类能力的**结果不变量**：返回版本必须严格推进，不诚实/缺失的版本折叠为 `internal_error`（否则旧 Ref 在 Core 眼里仍然「有效」，整套保护被静默破坏） |

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
- **`webview.page_state` 已有 wire payload**（`get_page_state` 命令 + `page_state` 结果，2026-10-01），
  命令路径与只读直连 API（`GetPageStateAsync`）共用同一套校验与调度。
- **WinUI `DispatcherQueue` 适配器**在 `Source/PuddingDesktop.CapabilityHost`（只引用 Contracts、无独立可测逻辑，
  契约由本组件的假调度器测试覆盖）；在 PuddingDesktop 里构造它并装配宿主属于切片 C-3/D。
