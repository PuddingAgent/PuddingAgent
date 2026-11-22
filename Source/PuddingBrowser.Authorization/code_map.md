# PuddingBrowser.Authorization — code_map
> 源指纹: *.cs=3-831c5d4b695d, *.csproj=1-21db8ad18fb5 · 条目数: 6 · 最近整理: 2026-10-19

> 单目标会话授权纯逻辑组件；由 DesktopService.BrowserPageLifecycle 持有，负责默认 Bridge 页面命令准入。
> 目标规则见 [ADR-097](../../Docs/07_architecture/111ADR-097Agent浏览器控制一期闭环与单一执行链ADR.md) 与[一期方案](../../Docs/12_features/Agent浏览器控制一期整体实施方案-2026-10-18.md)。

## 1. 编译边界

`PuddingBrowser.Authorization.csproj` 仅允许引用 `Pudding.Contracts`，禁止 PackageReference 与宿主/UI/驱动引用；`EnforceAgentTargetBoundary` 在 ResolveReferences 前拒绝越界引用。测试工程为 `Source/PuddingBrowser.AuthorizationTests/`，不依赖 Host 或 WinUI。

## 2. 文件与当前语义

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `AgentTargetSession.cs` | 会话身份：目标页 + 桥世代 + 宿主注入的可信控制句柄 | `AgentTargetSessionId`、`AgentTargetOwnerId`、`AgentTargetControlHandle` | `AgentTargetSessionAuthority.cs` | 快照含 `PendingTarget`（用户已交出、归属待确认）；展示名仅诊断用，不得当身份 |
| `AgentTargetAuthorization.cs` | 操作分类、九种稳定拒绝码与带写世代的唯一票据 | `AgentTargetOperation`、`AgentTargetErrorCodes`、`AgentTargetOperationExtensions` | `AgentTargetSessionAuthority.cs` | 票据释放后不可复用；推进写世代必须同时取消在途写 |
| `AgentTargetSessionAuthority.cs` | 单目标会话状态机：交页确认、暂停接管、准入复检与精确释放 | `AgentTargetSessionAuthority` | `Source/Pudding.DesktopService/code_map.md` | 不同 owner 一律拒绝；重复或迟到的完成不得释放他人占用 |
| `PuddingBrowser.Authorization.csproj` | 组件编译边界：仅允许引用 `Pudding.Contracts` | `—` | `Pudding.Contracts` | 禁止 PackageReference 与宿主/UI/驱动引用，越界由 `EnforceAgentTargetBoundary` 在 ResolveReferences 前拒绝 |
| `../PuddingBrowser.AuthorizationTests/AgentTargetSessionAuthorityTests.cs` | 状态机 48 例：准入与归属、两轴控制及取消释放 | `AgentTargetSessionAuthorityTests` | `AgentTargetSessionAuthority.cs` | — |
| `../PuddingBrowser.AuthorizationTests/ComponentBoundaryTests.cs` | 引用闭包检查：越界引用必须取红 | `ComponentBoundaryTests` | `PuddingBrowser.Authorization.csproj` | — |

## 3. 生产接缝

V1 内置 Agent 浏览器：首条可信 owner 的页面请求可隐式建立目标，同 owner 的后续页面请求可切换目标；不同 owner 拒绝。显式 Revoke 暂停隐式绑定，空 UI 快照不能调用 Revoke；页面关闭、断连使旧票据失效。上下文可信级别仍由 DesktopService 校验。

**已接入（2026-10-18，ADR-097 D3）**：`Pudding.DesktopService.BrowserPageLifecycle` 是唯一生产持有者；桥握手/断连、用户交页（`SetAgentTarget`）、暂停/接管、关页/关上下文、所有者确认（`TryClaimControlOwner`）、页面命令准入/复检/释放全部经其标量门面转发到本组件。Shell/桥侧不直接引用本组件（只拿 `Pudding.DesktopService` 的标量门面与稳定拒绝码），因此不破坏 Shell 引用白名单。

接入（I2）前须保持：会话有可信 Core 会话所有者；暂停/接管是独立两轴并推进写世代；票据唯一且释放后不可复用；旧新权威不能同时裁决。装配点是 Desktop 浏览器工作区生命周期 + `DesktopService` 的 Contracts/标量门面；组件先独立验证，再进宿主。
