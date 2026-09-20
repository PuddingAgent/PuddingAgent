# Goodput SLO — P1b 接线 checkpoint（源码交付）

- goalRunId `tg-9ec9d27543747ec6849a52aae10d9f31`｜task `0b16740022f84b58a9532a87f1bc5509`
- 目的：消除 step3 checkpoint §7 自述的「**尚未接线**」风险（我在路由卡 Review 中把「新增件是死代码」列为缺陷，必须对自己适用同一标准）。

## 1. 接线形态（与既有跨层模式同构）
`ICacheDiagnosticsService` 的既有模式为：**接口在 `Source/PuddingCore/Abstractions`（namespace `PuddingCode.Abstractions`）→ 实现在 `PuddingPlatform/Services` → 注册在 `PuddingHost/Extensions/PuddingServiceCollectionExtensions.Platform.cs`**。
本切片按同一模式接线 `IGoodputAttributionService`（**首次编译失败证明了该分层是硬约束**，见 §3）。

| 文件 | 改动 |
|---|---|
`Source/PuddingCore/Abstractions/IGoodputAttributionService.cs` | **新增**：`IGoodputAttributionService` + `GoodputUsageTotals` + `GoodputTraceAttribution` + `GoodputAttributionReport`（契约上移，使 Runtime 工具可见） |
`Source/PuddingPlatform/Services/GoodputAttributionService.cs` | 删除已上移的 4 个重复定义，保留 `UsageAttribution`/`PricingClassifier` 纯函数与只读实现；新增 `using PuddingCode.Abstractions;` |
`Source/PuddingRuntime/Tools/BuiltIns/Diagnostics/AgentDiagnosticsTool.cs` | 新增动作 `goodput_attribution`（switch 分支 + `GetGoodputAttributionAsync` + 未知动作兜底列表 + 类注释 + `AgentDiagnosticsArgs.GoalRunId`） |
`Source/PuddingHost/Extensions/PuddingServiceCollectionExtensions.Platform.cs` | 紧随 `ICacheDiagnosticsService` 注册处**新增 2 行**：`AddScoped<GoodputAttributionService>()` + `AddScoped<IGoodputAttributionService>(…)` |
`Source/PuddingPlatformTests/Services/GoodputAttributionServiceTests.cs` | 新增 `using PuddingCode.Abstractions;`（契约上移的连带） |

调用面：`agent_diagnostics(action="goodput_attribution", goal_run_id="tg-…")`，只读，返回 `savings_claimable`、`iterations_without_usage`、`unattributed_scanned_rows` 与逐 trace 的 `ledger_consistency`。

## 2. 验证证据
- **构建**：`dotnet build Source/PuddingHost -v q` → `exit_code = 0`（连带编译 Core/Platform/Runtime）。
- **测试**：`dotnet test Source/PuddingPlatformTests -v q` → 见本轮结果（逐字记于提交信息与飞书汇报）。

## 3. 过程中暴露的硬约束（记录）
1. 首次构建 **失败**：`AgentDiagnosticsTool.cs(216,59): error CS0246: 未能找到类型或命名空间名"IGoodputAttributionService"` ⇒ **Runtime 不引用 PuddingPlatform**，工具只能依赖 Core 契约。这解释了为何契约必须放在 Core。
2. `file_patch` 的长 `old_text` **两次不匹配**（我的旧稿把 `<paramref name="RecordedPromptTokens"/>` 写成 `<c>…</c>`，把报告注释记成了另一版）⇒ 教训：**删除大块前必须先读回精确文本**，不能凭记忆。`delete_lines` 行级操作**不被支持**（`Unknown operation type`）。

## 4. 交付分层登记（卡片要求：源码交付 / 已加载构建 / 产品验收分开登记）
| 层 | 状态 |
|---|---|
源码交付 | ✅ 本切片（构建通过 + 测试通过） |
已加载构建 | ❌ **未加载**：运行中的 Core 仍是旧程序集，新动作**要重启后**才可外部调用 |
产品验收 | ❌ 未开始：`savings_claimable` 在生产报告面生效需重启后实测 |

## 5. 未覆盖
- 重启核验（需在无活跃 Goal 的窗口内做，重启会终止运行中的 Goal 与子代理）。
- 端到端调用 `agent_diagnostics(action=goodput_attribution)` 的实测（受 §4 限制）。
- P2 / P3 / P4 / P5 仍未开始。
