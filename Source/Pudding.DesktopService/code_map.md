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
| `DesktopInteractionState.cs` | 暂停与用户接管两个**独立轴**：变更类能力被拒（`Paused`/`UserTakeover`，接管优先），只读能力仍可用（观测不打断用户）。**这两个轴不再是本类私有字段**：唯一事实源是 `DesktopService.Authority`（进程内那一个 `BrowserAutomationAuthority`），本类只是读投影 + 写入口（设计方案 §3.1）；原因标签按轴保留（权威只有一个 reason 槽位），仅用于显示，不参与判定。交互槽位（对话框/Picker 单窗口互斥）仍由本类持有 |
| `DesktopServiceOptions.cs` | `AllowedCapabilities`、`ShellCallerTrust`（默认 `Untrusted` ⇒ 对话框/Picker/剪贴板默认不开放） |
| `DesktopAcceptance.cs` | 两段式验收**判定**（强制规则的可执行版本）：`ready-for-external-deploy` / `in-product-functional-complete` / `external-lifecycle-verified` 各自的证据要求；**明确拒绝**「在可能仍跑旧代码的进程上声称新构建功能完成」与「内部 Agent 声称生命周期已验证」 |
| `DeliveryManifest.cs` | **交付清单**（机器可读交接事实）+ `DeliveryManifestValidator`：缺构建/提交、有失败项、`knownUnverified` 为 null（=没想过）、外部步骤未覆盖生命周期四步、内部声明后续阶段 ⇒ 一律不放行 |
| `DeliveryManifestBuilder.cs` | 清单**生成器**：事实 → 清单 → **同一套校验自检**（不合格不产出）；未验证项来自日志提取，生命周期四步自动补齐 |
| `KnownUnverifiedExtractor.cs` | 从修改日志提取「内部证明不了的结论」（保守词表、去重保序有界、**永不返回 null**） |
| `TrxTestResultParser.cs` | 从 TRX 读**真实**测试计数（读不出来就失败，不猜 0；多套件全有或全无） |
| `DeliveryFactCollector.cs` | 交付链**入口（内部一侧）**：从发布目录 / TRX 结果目录 / 日志目录采集事实 → 生成并自检清单；目录或事实缺失一律明确失败（产物只写文件名，不泄露本机路径） |
| `Browser/BrowserSmokePlan.cs` | 浏览器自动化**外部 smoke 清单**（G1–G11 的唯一事实源，13 项 S-01…S-13）：每项含门禁、触发方式、期望；`Validate` 要求**覆盖全部 G1–G11** 且结构完整；`ToMarkdown` 渲染给外部一方 |
| `Browser/ExternalAcceptanceSession.cs` | 外部验收**会话**：把「观察」变成判定（**未执行 ≠ 通过**）；四类明确拒绝（未运行、缺构建标识、缺证据、以内部声明替代外部观察） |
| `Browser/ExternalAcceptanceFacts.cs` | 外部观察**事实模型**（每条检查的观察值 + 备注）；序列化形状是 CLI 与脚本之间的唯一契约 |
| `Browser/AutomationDriverPolicy.cs` | 浏览器驱动**策略适配器**：把 `PuddingBrowser.Automation` 的计划/账本接到 `IBrowserDriverPolicy` 接缝上（组件不反向依赖 Automation） |
| `DesktopCapabilityHost.cs` | 宿主组合（切片 C-2）：只依赖 `IDesktopConnectionSupervisor` 端口 + `DesktopService`；启动时**二选一传输**（选旧 Bridge 则拒绝启动，不做跨传输回退）；同一 DesktopId **只允许一个活动传输**（进程级占用，停止即释放）；`StartAsync`/`StopAsync`/`WaitForStateAsync`；停止超时仍释放占用 |
| `DesktopChannelTransportResolver.cs` | 把 Core 发布的端点描述解析成 Desktop 传输（计划 §7）：版本不支持 / 形态未知 / 地址不可用一律明确失败；**凭据不从描述里取**（描述没有凭据字段，凭据只能由主机侧注入） |
| `DesktopCapabilityChannelSettings.cs` | `desktop.json` 对应段的**值对象**（不读配置文件，由宿主映射进来）：`Enabled` 缺省 false（继续走旧 Bridge）、`SectionName = "Desktop:CapabilityChannel"`、`DeclaredCapabilities` 是**代码事实**（实现即声明，不从配置放宽）、`CreateConnectionOptions` / `ResolveTransportFromDescription` |
| `DesktopCapabilityChannelPreflight.cs` | 「要不要启动能力通道」的**单一判定入口**：未启用 ⇒ 不启动；启用但端点描述缺失/不可解析 ⇒ **不启动且不回退**；否则可启动 |
| `DesktopCapabilityHostFactory.cs` | 用配置构造宿主：关闭 ⇒ 明确失败**且不构造**；启用 ⇒ 只构造**不启动**（启动名额留给组合根，见规格 §7.2 的陷阱） |
| `DesktopCapabilityChannelComposition.cs` | **组合根**（唯一会改变产品行为的一步）：判定 → 构造 → `StartAsync` → `DisposeAsync` 一次调用完成。关闭 ⇒ `Success(null)` 什么都不做；启用却起不来 ⇒ **明确失败**（不是只记日志）；不做跨传输回退；缺省服务策略 fail-closed。**只接受 `BrowserPageLifecycle`**（它持有唯一权威实例与准入读的页面注册表）：分开传"注册表 + 权威"就允许配错对，那等于又出现两份控制权状态（设计方案 §3.1） |
| `BrowserAdmissionAdapter.cs` | **两传输共用准入适配器**（方案 §4.2）：入队前 `Admit`、执行前 `Revalidate`、`finally` `Release`（幂等）；两条传输收敛到同一 `BrowserPageLifecycle` 与同一请求形状，判据不在此处而唯一 authority 内；只读观察不产生租约，**写操作必须有租约** |
| 控制面准入与声明（`DesktopCapabilityPolicy.cs` / `DesktopCapabilityChannelSettings.cs`） | 两个 `browser.binding.*` 能力的**准入规则**（`AllowedTrust` 仅 `AgentAuthorized`；纳入 `IsBrowserContextScoped`）与**声明**（实现即声明，与 `DesktopCapabilities.AllCapabilities` 相等）；策略表冻结快照随有意变更同步 |
| `BrowserTaskBindingService.cs` | **任务绑定的受理与撤销**（§4.1）：只有目标**当下**正是用户选定给 Agent 的那一页才签发（**绑定不能替代用户选择**）；重复受理先撤旧授权（避免同 (任务,目标) 并存两份）；**确定性撤销入口**：`Revoke`（该绑定）／`RevokeForTarget`（切目标：撤该页全部绑定，返回条数、幂等）／`RevokeAllForCaller`（任务结束/父撤销：按行动者+可选运行撤）。策略不在此重写，交 `BrowserGrantIssuer`／唯一 authority |
| `BrowserGrantIssuer.cs` | **授权签发的生产路径**（方案 §4.1；此前 `IssueGrant` 没有生产调用方）：校验可信行动者、有效期上限、范围非空、子代理范围必须是父授权子集、目标必须是**已登记**页面、连接世代必须存活 ⇒ 才调用唯一 authority 的 `IssueGrant`；每条拒绝都给具体原因，**不**降级签发 |
| `BrowserPageLifecycle.cs` | 页面/上下文生命周期的**唯一所有者**（设计方案 §3.3）：**创建侧** `RegisterContext`/`RegisterPage`/`SetAgentTarget`，**关闭侧** `ClosePage`/`CloseContext`（两个注册表 + 该页**全部**授权 + 写租约）。**登记原子性**：任一侧失败都回收另一侧并抛出 ⇒ 调用方永远拿不到"半登记"页面（视图侧有、准入侧没有，或反之）。`SetAgentTarget` 切换时撤销**旧目标**的执行权并推进授权世代，但**保留只读**（§3.2）。`Create(...)` 为进程构造唯一端口（含唯一权威与两个注册表，调用方不必引用 Automation 组件）；另提供控制面门面 `IsPaused`/`IsUserTakeover`/`SetPaused`/`SetUserTakeover`。**失败安全**：注册表里已经没有它也照样撤授权。结果按项如实报告（`WasRegistered`/`PagesCleared`/`GrantsRevoked`/`LeaseReleased`/`ExecutionScopeRevoked`）。还持有**读取基线存储** `Readings`（§145）并在 `ClosePage`/`CloseContext` 里清除——"页面没了、内容还留着"与"授权没撤"同类 |
| `BrowserReadingBaselineStore.cs` | 读取基线存储（§145）：保存"某修订标识对应的那份文本"，让 `since_revision` 能给出真正的行级差异。三条约束：**按页隔离**（跨页比较无意义）、**有界**（每页只留最近若干份；单份超过 `MaxLinesPerBaseline` 就**不存**——保存被截断的基线会算出错误差异）、**随生命周期清除**（由 `BrowserPageLifecycle` 持有并清理） |
| `DesktopShellSurface.cs` | 把两个 Shell 端口（交互设施 + 宿主告知）适配成表面：预算纵深防御（剪贴板二次截断）、异常折叠为 `internal_error`、取消原样传播；**不补齐**自动化状态与页面数（那是 `DesktopService` 的权威状态） |
| `BrowserTargetRegistry.cs` | 浏览器目标注册表（`IDesktopBrowserTargetRegistry` 实现）：上下文可信级别 / Agent 目标 / 活动页。`SetAgentTarget` 是**撤销的**一等操作（`RegisterPage(isAgentTarget:false)` 只增不减，撤销缺失会让注册表替不该被驱动的页面背书）。`RegisterContext` 与 `DesktopTargetRegistry` **同一条规则**（同级别幂等、换级别抛异常）——两个注册表对同一事实的规则必须一致，它们曾在"页面重复登记是否清除 Agent 目标"上分叉。`IsRegistered` 供"登记失败不得留半登记状态"的验证使用 |
| `DesktopTargetRegistry.cs` | 页面/上下文登记表（准入读的那一份）：版本只前进、可信级别不可由调用方声明；另承载**用户选定的 Agent 目标页**（`SetAgentTarget`，同一时刻至多一个，未登记/已关闭的 id 不标记）——准入的 page 级闸门读它。**登记对已发布事实非破坏性**：重复登记只合并新信息 —— 版本只进不退、`Readiness=Unknown`/`isAgentTarget=null` 表示"不表态"（保留原值），否则一次重复的生命周期事件就会静默关掉页级闸门或让旧 ref 复活；`RegisterContext` 同级别幂等，换级别抛异常（否则是 Untrusted→AgentAuthorized 的静默提权）；`AgentTarget`（当前目标，切换时要先取它）与 `AgentTargetCount`/`OpenPageCount` **分别统计**（设计方案 §3.3） |
| `DesktopCapabilityPolicy.cs` | 能力 × 目标可信级别的准入策略表；`RequiresAgentTarget` 由信任表**推导**（需要页面目标且只对 AgentAuthorized 开放），避免出现第二份真源 |

> 变更类后置条件已**移出**本组件：原 `DesktopMutationInvariants.cs`（「所有 mutating 结果版本必须严格递增」）
> 于 2026-10-08 删除，改由 `PuddingBrowser.Automation` 的 `BrowserMutationPostcondition` 统一判定，
> DesktopService 与 Bridge **共用同一份**纯逻辑。原因：该通用要求把 `fill`/`click`（只开菜单）等
> 不推进版本的成功动作判成 `internal_error`（实测假失败），见
> [设计方案 §1.1](../../Docs/12_features/浏览器自动化可靠性与渐进阅读设计方案-2026-10-08.md)。

## 错误映射（终态语义）

| 情形 | 结果 |
|---|---|
| 目标未登记/已关闭 | `invalid_target`（不触碰 UI） |
| 可信级别不足（含工作台脚本注入） | `unauthorized` |
| **页面不是用户选定的 Agent 目标页**（page 级闸门） | `unauthorized`（不触碰 UI；只影响「需要页面目标且只对 AgentAuthorized 开放」的能力） |
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
- **page 级闸门（2026-10-08）只覆盖「只对 AgentAuthorized 开放」且需要页面目标的能力**：
  `webview.execute_javascript`、`browser.snapshot/locate/interact/wait_for/tabs`。
  `webview.navigate` 与 `webview.page_state` 对普通网页与工作台也开放（通用浏览），**刻意不在**闸门内；
  把导航也收紧到「目标页」属于设计 §3.2 的写权限/grant 切片。
  ⚠️ **这是产品行为变更**：未选定 Agent 目标页时，上述能力一律 `unauthorized`
  （设计 §3.2「默认只授权用户选定的目标页」）。Shell 侧由
  `DesktopApplicationCoordinator.Capability` 把 `workspace.AgentTargetPageId` 同步进注册表。
