# PuddingBrowser.Authorization — code_map

> Agent 浏览器**目标授权**组件（纯逻辑叶子）：把「用户在 Desktop 上选定某页」翻译成**一个**授权判定。
> 设计依据：浏览器控制简化架构方案（`Docs/11_design/浏览器控制简化架构方案-2026-10-13.md`）。
> 交付状态：**S1–S3 已完成（2026-10-13）；S5 未开始** —— 组件与测试工程已登记
> `PuddingAgentNetwork.slnx`，**尚未被任何宿主引用**；旧授权栈保持原状。

## 1. 边界（编译期强制）

- **唯一允许的引用**：`Pudding.Contracts`（BCL-only 叶子）。**禁止**任何 `PackageReference`。
- **禁止引用**：UI / 驱动（`PuddingBrowser.WebView2`、`PuddingBrowser.WinUI`、`PuddingDesktop*`）、
  映射层（`Pudding.DesktopService`、`Pudding.DesktopSurface.Browser`、`PuddingBrowser.Automation`）、
  宿主（`PuddingHost`、`PuddingRuntime`、`PuddingAgent`）。
- `EnforceAgentTargetBoundary`（`.csproj`，`ResolveReferences` 前）**可取红**：临时加一条指向
  `PuddingHost` 的引用，构建报 `may reference Pudding.Contracts ONLY (component boundary, S1/S4)`
  并失败（已实测：1 个错误；还原后 0 警告 0 错误）。
- 测试工程另做**运行时**断言：组件与测试工程的引用闭包都不得出现宿主/UI/驱动程序集。

## 2. 文件与职责

| 文件 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `AgentTargetSession.cs` | 唯一授权载体与 UI 快照 | `AgentTargetSession` / `AgentTargetSessionId` / `AgentTargetSessionSnapshot` | `AgentTargetSessionAuthority.cs` | 会话 = 用户选中的一页 + 桥世代；不含身份/任务/读写分级/过期/租约 |
| `AgentTargetAuthorization.cs` | 拒绝码、操作类别与准入票据 | `AgentTargetErrorCodes` / `AgentTargetDenial` / `AgentTargetOperation` / `AgentTargetPermit` | `AgentTargetSessionAuthority.cs` | 六个码即线名，UI 与工具必须复用；票据不是 grant 也不是 lease |
| `AgentTargetSessionAuthority.cs` | 唯一决策点（状态机） | `OnBridgeReady` / `OnBridgeLost` / `SelectTarget` / `Authorize` / `Revalidate` | `AgentTargetSession.cs` | 身份不参与授权；换页原子替换；接管只翻转布尔、不推进控制世代 |
| `...AuthorizationTests/AgentTargetSessionAuthorityTests.cs` | 状态机不变式（21 条） | `AuthorityIsNotLiveBeforeTheHandshake` / `TakeoverBlocksSideEffectsButKeepsObservation` | `ComponentBoundaryTests.cs` | 变异取红：移除传输门槛 ⇒ 首个用例变红（已实测） |
| `...AuthorizationTests/ComponentBoundaryTests.cs` | 运行时依赖方向断言 | `TheComponentReferencesContractsOnly` / `TheStateMachineIsTestableWithoutAnyHost` | `PuddingBrowser.AuthorizationTests.csproj` | 加一条指向宿主的引用即变红 |

## 3. 关键语义（表格放不下的判据）

- **判定只回答一件事**：「用户此刻是否把这一页交给了 Agent，且桥仍是当初那一代」。
  它**不回答**「调用者是不是 Agent」——身份只用于审计。理由：把身份当门槛既挡不住提示注入，
  又会在 Ambient 上下文丢失时把本地浏览器整体关死（方案 §9.2）。
- **接管是运行控制，不是权限审批**：`BeginUserControl` / `EndUserControl` **只翻转布尔**，
  **不推进控制世代**。若推进，在途写会报 `agent_target_revoked` 而非 `browser_busy`，
  且只读票据会被误判过期 —— 违反 69 规格「接管不撤销阅读」。
- **控制世代只由「目标作废」推进**：撤销 / 换页 / 桥世代变更或断开 / 关闭。
  因此 `Authorize` 与 `Revalidate` 的两段式判定能拦住「判定通过 → 期间被接管或换代」的竞态。
- **审计事实不抹除**：换页后 `LastRevokedSessionId` 保留被替换会话的 id；
  `GateUnsafe` 用它区分「从未选过页」（`agent_target_required`）与「选过但被撤销」（`agent_target_revoked`）。
- **票据不是 grant**：`AgentTargetPermit` 无持有者身份、无期限、无所有权，
  仅携带会话 id + 桥世代 + 控制世代 + 是否副作用，用于复检竞态。
- **副作用串行**：同一时刻最多一张票据在途；`CompleteOperation` 幂等清理，不误清新票据。
- **高风险必须确认**：只有 `HighRiskAction` 需要一次性用户确认；
  分类层**必须**把无法可靠判定的不可逆写归入该类（fail-closed），不得为求方便归入 `Mutate`。

## 4. 尚未接线（S5 前置）

- **`OnBridgeReady` / `OnBridgeLost` 没有生产调用者**：这正是当前「Core 已连接、桌面权威恒不活」的根因。
  既有 `NotifyConnectionGeneration` 在生产代码 **0 调用者**（已双重证实：`git grep` 26 处命中中 23 处在测试，
  `code_callers` = 0）。
- **Hello Ack 不携带世代**：`BrowserBridgeHelloAck` 仅 `ProtocolVersion/Accepted/ErrorCode/ErrorMessage`
  ⇒ 接线前**必须先改协议**（方案 step 2）。
- **UI 与工具路由都未改**：`AssignAgentTargetAsync` 目前只写标记；工具仍走旧栈。
- 取代对象清单见方案 §10.5（约 20 个文件，属 step 4，需裁定后执行）。
