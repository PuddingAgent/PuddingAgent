# PuddingBrowser.Authorization — code_map

> 单目标会话授权纯逻辑组件；当前只被自身测试工程引用，尚非产品执行权威。
> 目标规则见 [ADR-097](../../Docs/07_architecture/111ADR-097Agent浏览器控制一期闭环与单一执行链ADR.md) 与[一期方案](../../Docs/12_features/Agent浏览器控制一期整体实施方案-2026-10-18.md)。

## 1. 编译边界

`PuddingBrowser.Authorization.csproj` 仅允许引用 `Pudding.Contracts`，禁止 PackageReference 与宿主/UI/驱动引用；`EnforceAgentTargetBoundary` 在 ResolveReferences 前拒绝越界引用。测试工程为 `Source/PuddingBrowser.AuthorizationTests/`，不依赖 Host 或 WinUI。

## 2. 文件与当前语义

| 文件 | 职责与限制 |
|---|---|
| `AgentTargetSession.cs` | 当前目标页 + 桥世代的会话与快照；当前不包含可信 Core 会话所有者 |
| `AgentTargetAuthorization.cs` | 操作分类、六种拒绝码、请求及票据；当前票据无唯一 operationId |
| `AgentTargetSessionAuthority.cs` | OnBridgeReady/Lost、选页、撤销、Authorize/Revalidate/Complete；当前接管只翻转布尔值，完成仅按会话与控制世代匹配 |
| `PuddingBrowser.Authorization.csproj` | 组件依赖门禁 |
| [状态机测试](../PuddingBrowser.AuthorizationTests/AgentTargetSessionAuthorityTests.cs) | 独立准入、串行、接管、切页与换代用例 |
| [边界测试](../PuddingBrowser.AuthorizationTests/ComponentBoundaryTests.cs) | 引用闭包检查 |

## 3. 生产接缝

本组件无生产消费者。现有 Bridge 握手/断连已通知旧 `BrowserPageLifecycle`，并不是调用这里的 OnBridgeReady/Lost；不要再将旧权威的历史“零调用者”结论用于当前代码。

接入前须满足一期方案的所有者、独立写世代与精确票据释放规则。接管后恢复旧写、A 的迟到 Complete 清掉 B 占用的具体静态反例见[审阅报告 F7](../../Docs/14_reports/Agent浏览器控制架构与实现审阅-2026-10-18.md#f7--p1候选简化授权组件尚未接入且不能直接替换)。组件先独立验证，再通过 DesktopService 的 Contracts 门面装配到 Shell；旧新权威不能同时裁决。
