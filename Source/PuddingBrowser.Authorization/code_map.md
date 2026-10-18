# PuddingBrowser.Authorization — code_map

> 单目标会话授权纯逻辑组件；当前只被自身测试工程引用，尚非产品执行权威。
> 目标规则见 [ADR-097](../../Docs/07_architecture/111ADR-097Agent浏览器控制一期闭环与单一执行链ADR.md) 与[一期方案](../../Docs/12_features/Agent浏览器控制一期整体实施方案-2026-10-18.md)。

## 1. 编译边界

`PuddingBrowser.Authorization.csproj` 仅允许引用 `Pudding.Contracts`，禁止 PackageReference 与宿主/UI/驱动引用；`EnforceAgentTargetBoundary` 在 ResolveReferences 前拒绝越界引用。测试工程为 `Source/PuddingBrowser.AuthorizationTests/`，不依赖 Host 或 WinUI。

## 2. 文件与当前语义

| 文件 | 职责与限制 |
|---|---|
| `AgentTargetSession.cs` | 会话 = 目标页 + 桥世代 + **宿主注入的可信控制句柄**（Core 会话所有者 + 仅诊断用的展示名）；`AgentTargetOwnerId`/`AgentTargetControlHandle`；两轴运行控制态 `Paused`/`UserTakeover`；控制面快照 |
| `AgentTargetAuthorization.cs` | 操作分类、九种稳定拒绝码、带可信控制句柄的请求、票据（唯一 `AgentTargetPermitId` + 连接/控制/**写世代** + 取消信号）、`AgentTargetWriteEpoch` |
| `AgentTargetSessionAuthority.cs` | OnBridgeReady/Lost、选页（幂等/换目标/换所有者原子替换）、撤销/关页/所有者失效、暂停与接管两轴、Authorize/Revalidate/CompleteOperation/CancelOperation；进入阻断推进写世代并取消在途写，释放后票据不可再用，重复/迟到完成不释放他人占用 |
| `PuddingBrowser.Authorization.csproj` | 组件依赖门禁 |
| [状态机测试](../PuddingBrowser.AuthorizationTests/AgentTargetSessionAuthorityTests.cs) | 40 条独立用例：准入、串行、归属、两轴控制、切页/换代、F7 反例、取消与精确释放 |
| [边界测试](../PuddingBrowser.AuthorizationTests/ComponentBoundaryTests.cs) | 引用闭包检查 |

## 3. 生产接缝

本组件**无生产消费者**（`Source/`、`Tests/` 内除组件自身与测试工程外无引用）。现有 Bridge 握手/断连通知的是旧 `BrowserPageLifecycle`，不是调用这里的 OnBridgeReady/Lost；旧权威的历史“零调用者”结论不适用于本组件。

接入（I2）前须保持：会话有可信 Core 会话所有者；暂停/接管是独立两轴并推进写世代；票据唯一且释放后不可复用；旧新权威不能同时裁决。装配点是 Desktop 浏览器工作区生命周期 + `DesktopService` 的 Contracts/标量门面；组件先独立验证，再进宿主。
