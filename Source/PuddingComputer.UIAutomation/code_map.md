# PuddingComputer.UIAutomation
> 源指纹: Source/PuddingComputer.UIAutomation/**=11-0027736aefda · 条目数: 10 · 最近整理: 2026-11-22
> 定位：Computer Use 的无障碍发现层（net10.0-windows）。附加已确认窗口、有界枚举子树、报告可用性。
> 依赖方向由 csproj 的 `AllowedProjectReferences` 与 `BoundaryRules` 双重强制：只允许 `Pudding.Contracts` 与 FlaUI。

## 入口与配置

观察入口是 `FlaUiInspector.Capture`（配合 `UiaCaptureOptions` 给上限）；写入入口是 `UiaValueWriter`。
选择器匹配只有一份实现 `UiaNodeMatching.MatchesFields`，观察层与写入层共用。

## 文件

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `BoundaryRules.cs` | 依赖边界以可执行规则声明 | `BoundaryRules`、`FindViolations`、`ForbiddenPrefixes` | — | 黑名单先于白名单判定；只维护白名单会让新的同名层被放行 |
| `FlaUiInspector.cs` | 基于 FlaUI.UIA3 的有界只读观察 | `FlaUiInspector`、`Capture`、`IUiaInspector` | `UiaCaptureOptions.cs` | 取根前必须先唤醒 UIA2 客户端；否则 Chromium 窗口会缓存空子树，整次采集白做 |
| `SelectorResolution.cs` | 命中数与截断共同判定解析结论 | `SelectorResolution`、`Decide`、`ResolutionOutcome` | `UiaNodeMatching.cs` | 遍历被截断时不得给出 Ok 或 NotFound |
| `UiaAvailabilityClassifier.cs` | 观察结果折算成可用性四态 | `UiaAvailabilityClassifier`、`Classify`、`SparseThreshold` | `UiaContracts.cs` | 超时优先于错误与零节点；先判后两者会把慢应用误报成不支持无障碍 |
| `UiaCaptureOptions.cs` | 枚举深度与节点数及时长上限 | `UiaCaptureOptions`、`Validate`、`EffectiveTimeout` | `FlaUiInspector.cs` | 超限要作为状态回到调用方；静默截断会让上层以为界面只有这么多元素 |
| `UiaContracts.cs` | 快照与节点及可用性的契约记录 | `UiaSnapshot`、`UiaNode`、`UiaAvailability` | `FlaUiInspector.cs` | 节点顺序必须父在子前；这是 BFS 的前提，改成 DFS 要逐点重排 |
| `UiaNodeMatching.cs` | 选择器与观察节点的字段级匹配 | `UiaNodeMatching`、`MatchesFields`、`FindAll`、`TryFindUnique` | `UiaContracts.cs` | 匹配规则只有这一份；两层各写一套会让可观察与可写入给出不同答案 |
| `UiaPreprobe.cs` | Chromium 窗口的 UIA2 预探唤醒 | `UiaPreprobe`、`Probe`、`Wake` | `FlaUiInspector.cs` | 唤醒失败不阻断主流程；真实性由后续匹配与读回判定 |
| `UiaStabilization.cs` | 无障碍树预热窗口与轮询判据 | `UiaStabilization`、`ShouldKeepWarming`、`EffectiveWarmup` | `UiaPreprobe.cs` | 预热窗口用尽就照常采集；把等待当成功等于自造可用性 |
| `UiaValueWriter.cs` | 写值与调用模式的操作及读回核对 | `UiaValueWriter`、`WriteValue`、`Invoke`、`UiaWriteStatus` | `UiaNodeMatching.cs` | 只有读回一致才算成功；截断遍历单列 PartialTree，归入 NotFound 会把没看完伪装成没有 |

## 约束与坑

- 观察层只读：不点击、不输入、不排序、不重试；写入与调用只在 `UiaValueWriter`，且以读回一致为成功判据。
- 读到的节点少有两种成因：应用真没有无障碍，或**读得太早**。判据在 `UiaStabilization` 与 `UiaAvailabilityClassifier` 两处，不要合并。
- 依赖清单变更必须同时改 csproj 注释、`BoundaryRules` 与 `BoundaryTests`；只改一处会让边界门禁与实际引用脱节。
