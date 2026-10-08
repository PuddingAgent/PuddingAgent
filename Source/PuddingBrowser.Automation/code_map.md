# PuddingBrowser.Automation — code_map

> 浏览器自动化**可靠性**组件（叶子）：统一控制权、页面授权、执行租约、证据化回执与重试裁定。
> 设计依据：[浏览器自动化可靠性与渐进阅读设计方案](../../Docs/12_features/浏览器自动化可靠性与渐进阅读设计方案-2026-10-08.md)（§3/§4，切片 **B0**）。
> 交付状态：**S1–S4 已完成；S5 已部分落地（2026-10-08）** —— 组件与其测试工程已登记
> `PuddingAgentNetwork.slnx`，`Pudding.DesktopService` 与 `PuddingHost` 已加显式引用（共用同一份
> 变更类后置条件）。DI 组合根装配唯一 `IBrowserAutomationAuthority` 实例仍属后续切片。

## 1. 边界（编译期强制）

- **唯一允许的引用**：`Pudding.Contracts`（BCL-only 叶子，自身 `ProjectReference` = 0）。
- **禁止引用**：UI / 驱动（`PuddingBrowser.WebView2`、`PuddingBrowser.WinUI`、`PuddingDesktop*`）、
  映射层（`Pudding.DesktopService`、`Pudding.DesktopSurface.Browser`）、宿主（`PuddingHost`、`PuddingRuntime`、`PuddingAgent`）。
- `EnforceAutomationBoundary`（`PuddingBrowser.Automation.csproj`）在 `ResolveReferences` 前取红：
  临时加一条指向消费方的 `ProjectReference` ⇒ **构建立刻失败**（已实测）。
- `Source/PuddingBrowser.AutomationTests/ComponentBoundaryTests.cs` 另做三重运行时断言：
  探测器自检、进程未加载禁用程序集、`*.deps.json` 依赖闭包；并直接断言两个 csproj 的引用清单。

## 2. 文件与职责

| 文件 | 职责 / 不变量 |
|------|----------------|
| `BrowserAutomationAuthority.cs` | 实现 `IBrowserAutomationAuthority` + `IBrowserAutomationControl`：唯一事实源。接管/暂停/恢复/关闭/连接世代改变推进**控制世代**；撤销授权、切目标、关闭、世代改变推进**授权世代**；接管**不**推进授权世代。`Admit` 在入队前校验并签发写租约，`Revalidate` 在触碰页面前复检租约世代 ⇒ 「验证通过 → 期间被接管」必然被拦下。同一页面同时只有一个写租约；授权被撤销时租约随之释放。 |
| `BrowserGrantIssuancePolicy.cs` | 子代理授权的**上界**判据：子代理 grant 的范围必须 ⊆ 父任务在同一页面/同一 frame 的 grant；父任务无授权 ⇒ 子代理不得凭空获得。失败时抛 `ArgumentException`（fail closed）。 |
| `BrowserReceiptJudge.cs` | 证据 → 回执。只按「执行阶段 + 声明的后置断言」判定，**不按动作名推断导航**；未执行/结果不明 ⇒ `not_requested`；需要断言但拿不到 ⇒ `unverified`；有未通过断言 ⇒ `failed`。 |
| `BrowserOperationLedger.cs` | operationId 账本：键 = 调用身份 + 连接世代 + operationId，另存请求摘要。在途项**永不因容量被淘汰**（容量耗尽拒新写请求）；终态 tombstone 保留到容量压力出现，因此过期重传返回 `receipt_expired` 而不是被当成新动作。默认 5 分钟 / 1,024 项。 |
| `BrowserMutationPostcondition.cs` | 变更类动作的**后置条件**（取代「所有 mutating 结果版本必须严格递增」）：没有活版本是真失败；观测到文档提交即满足任何期望；<b>未声明后置条件时版本不推进不算失败</b>（fill/click 开菜单/SPA 更新）；只有显式声明 `DocumentNavigation` 未满足才报 `outcome_unknown`。`ValidateTabs` 不拿「另一页的版本推进」当证据，改为校验 `close` 是否真的关掉、`activate`/`new` 是否回带活版本。 |
| `BrowserRefIdentity.cs` | 引用身份模型（设计 §5 / G4）：`BrowserRefScope`（context/page/frame 三元组，frame=null 即顶层）、`BrowserObservationRevisions`（**三个分开的事实**：文档世代 / DOM 修订 / 视口修订）、`BrowserRevisionPolicy`（只读**不推进任何**事实；文档提交推三者；DOM 变更只推 dom；滚动只推 viewport）、`BrowserRefInvalidation`（分类作废）、`BrowserRefDenial` + 线名、`BrowserRefToken`（宿主绑定节点键 + 最小刷新提示）、`BrowserRefDecision` |
| `BrowserRefRegistry.cs` | 宿主绑定的引用登记表：身份用**宿主侧**节点键（`data-pudding-ref` 之类页面可自写属性只能当提示）；校验同时看作用域与文档世代 ⇒ **跨页/跨 frame 即使版本号相同也拒绝**；撤销**分类**（整页 / 单 frame / 单引用），而普通 DOM 变更与滚动**不作废任何**引用；reference 值不可解析，**绝不按键名/角色自动重绑** |
| `BrowserInteractionReceipt.cs` | 交互回执**判定**（设计 §4.1/§4.2、G7）：把「调用方声明的期望 + 驱动回读的观测值」判成断言与完成度。① 不按动作名推断成功——只有声明了期望（显式 `expect_value`，或 select 的 `values`、check/uncheck 的 `checked` 这类调用方自给的目标值）才有断言，否则如实报 `dispatched`；② **表单值一律不回显**（value/selectedValues 只给 matched 与长度）；③ 期望存在但拿不到观测 ⇒ `unverified`（不是 failed）。`ExecutionFor(error)` 由错误目录推导执行阶段 |

## 3. 契约落点（在 `Pudding.Contracts/Desktop/`，BCL-only）

| 文件 | 内容 |
|------|------|
| `BrowserControlContracts.cs` | 控制/授权世代、执行租约、`BrowserControlSnapshot`、`IBrowserAutomationAuthority` / `IBrowserAutomationControl` 两个窄端口、`BrowserAutomationOperation` + `BrowserOperationPolicy`（§3.2 表格的唯一真源）、`BrowserAutomationRequest` / `BrowserAuthorizationDecision` |
| `BrowserGrantContracts.cs` | `BrowserPageGrantScope`（read/write/manage）、`BrowserCallerIdentity`（含子代理父任务）、`BrowserPageGrantBinding`（实例+进程+连接世代）、`BrowserPageGrant`、`BrowserManagementGrant`（任务级管理授权）、`BrowserAuthorizationDenial` + 线名 |
| `BrowserActionReceiptContracts.cs` | 执行/验证/完成三组枚举与线名、`BrowserActionAssertion`（长度上限 + 敏感字段只留 length）、`BrowserObservationStamp`、`BrowserActionEffectSummary`、`BrowserActionReceipt`（completion/ok/safeToRetry 由入参派生，不可自相矛盾） |
| `BrowserRetryContracts.cs` | `BrowserRetryDisposition` + 线名、`BrowserRetryDecision`（纯数据；判定在组件内） |

**刻意不改动**：`DesktopCapabilityErrorCode` 与 `DesktopCapabilityErrors` 的线缆错误表（`ErrorTaxonomyTests` 对其做快照）。
线缆可见的原因码属于**接入切片**，B0 只在新增契约里给结构化拒绝原因。

## 4. 与消费方的关系

- **已接线（S5，2026-10-08）**：`Pudding.DesktopService`（Navigate/Interact/Tabs 后置条件）与
  `PuddingHost`（Bridge 的 `InteractAsync`）都调用 `BrowserMutationPostcondition`，两条传输因此
  **同语义**；原 `Source/Pudding.DesktopService/DesktopMutationInvariants.cs` 已删除。
- **尚未接线**：三份互不相通的 takeover/paused 状态（Shell 只写最不权威的一份，见
  `Docs/09_audit/2026-10-08-浏览器控制权与准入闸门核验.md`）尚未收敛到本组件的唯一
  `IBrowserAutomationAuthority` 实例；`IBrowserAutomationControl` 也没有 UI 侧驱动者。
- 回执（`BrowserActionReceipt`）目前只在组件内被判定与测试，尚未进入
  `DesktopInteractionResult` 或工具输出 —— 那需要工具层新增 `expect` 参数，属后续切片。

## 5. 测试

`Source/PuddingBrowser.AutomationTests/`（只引用本组件）：173 用例。

| 文件 | 覆盖 |
|------|------|
| `BrowserInteractionReceiptTests.cs` | 交互回执（G7）：`expect_value` 匹配 ⇒ verified（不匹配 ⇒ failed）、**表单值零回显**（只给 matched 与长度）、无期望 ⇒ dispatched 不假装成功、有期望但拿不到观测 ⇒ unverified、select/check 用调用方自给的目标值判定、执行阶段由错误目录推导、未开始+可重试才 safeToRetry |
| `BrowserRefIdentityTests.cs` | 引用身份与三个事实（设计 §5 / G4）：跨页/跨 frame（含「两页都恰好 v7」）、同角色同名字的新节点**不让旧引用复活**、整页/单 frame/单引用分类作废、普通 DOM 变更与滚动**不作废任何**引用、宿主存活校验、视口变化只让坐标失效、只读调用**零推进** |
| `BrowserMutationPostconditionTests.cs` | 变更类后置条件：fill/click 不推进版本仍判成功、无活版本是真失败、显式声明导航未满足报 `outcome_unknown`、tab close 按「是否关掉」判、关最后一页无剩余版本仍成功 |
| `BrowserControlAuthorityTests.cs` | G1：接管/暂停拒绝新写步骤、只读仍可用、接管不撤销阅读授权、接管确认后复检失败、恢复不复活旧租约/世代推进、关闭与连接世代改变作废全部许可、租约按页串行与重传复用 |
| `BrowserPageGrantTests.cs` | G2：同 context 非目标页拒绝、read 不授权 write、TabClose 需 manage+read、撤销/切目标/跨实例/跨 frame 拒绝、子代理不得借用或扩大父授权、任务级管理授权 |
| `BrowserReceiptJudgeTests.cs` | G3：fill/select 同文档 verified、click 菜单不因版本未增假失败、断言失败是 failed、超时已提交是 dispatched+unverified、取消未派发可安全重试、断连 unknown |
| `BrowserRetryPolicyTests.cs` | 重试裁定矩阵与线名稳定性 |
| `BrowserOperationLedgerTests.cs` | 同 ID 重传驱动执行次数 ≤ 1、同 ID 不同请求拒绝、`receipt_expired`、容量耗尽拒新写且不淘汰在途、tombstone 回收、只读查询入口 |
| `BrowserOperationPolicyTests.cs` | §3.2 表格的机器可验快照 + 枚举覆盖完整性（新增操作必须显式分类） |
| `BrowserAssertionRedactionTests.cs` | 长度上限、敏感字段零回显、日志摘要不含值、线名稳定性 |
| `ComponentBoundaryTests.cs` · `ComponentSurfaceTests.cs` | S3/S4 边界断言（含探测器自检与取红）、公开面冻结、两窄端口成员冻结、逐类**用例守恒** |

## 6. 已知边界（诚实登记）

- 写操作的**排队**不在本组件：`Admit` 对同页第二个写操作返回 `page_busy`，队列由 Desktop 映射层在 B1 决定。
- 上下文级管理授权（`ContextCreate/ContextClose/TabNew`）由**任务级** `BrowserManagementGrant` 表达，
  因为这些操作发生时还没有可授权的页面；按 context 粒度的 grant 由 B1 的生命周期端口决定。
- 账本不保证跨进程 exactly-once（与设计一致）。
- `BrowserMutationPostcondition` 目前只在**没有**显式后置条件的情况下判定成功/失败；
  `BrowserInteractRequest` 还没有 `expect` 参数，因此 `DocumentNavigation` 目前只被 navigate 使用。
  工具层暴露 `expect` 属后续切片。
- **`BrowserRefRegistry` 尚未被驱动消费**（2026-10-08，诚实登记）：它是设计 §5 的契约与判据层。
  驱动侧**已**用同语义的**页面作用域 token** 关掉了「跨页/跨 frame 同版本号命中」这一条
  （ref 线格式 `p{页面token}-v{文档世代}-n{序号}`，见 `Source/PuddingBrowser.WebView2/` 的 code_map），
  但**尚未**消费本登记表，因此以下两点仍未闭合：
  ① **宿主绑定的节点身份**：节点标记仍是页面可自写的 `data-pudding-ref`；真正的宿主身份需要
  CDP `backendNodeId`，而 CDP 在本驱动里仍是 `NotSupportedException("CDP not in Phase 2A-1")`（属 B3）；
  ② **宿主存活校验**（节点是否仍 connected / 仍在同一文档）与三个事实的分别维护。
  接入方式：`WebView2BrowserPage` 持有登记表，在 snapshot/locate 返回 ref 时按自己的 scope 登记，
  交互前用 `Validate`/`ValidateForCoordinates` 复检；届时驱动内那份 token 校验可以退化为快速路径。
