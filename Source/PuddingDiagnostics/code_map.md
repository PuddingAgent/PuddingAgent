# PuddingDiagnostics 代码地图（可诊断基础设施叶子组件）

> 定位：把「为什么这次调用失败」变成**可计算、可断言、可复现**的事实。设计见
> [`Docs/12_features/可诊断基础设施设计-2026-10-07.md`](../../Docs/12_features/可诊断基础设施设计-2026-10-07.md)；
> 交付状态按 [`组件化交付规程`](../../Docs/10_conventions/组件化交付规程.md) S1–S5 记录在该设计文档 §7。

## 1. 组件定位与边界（不可协商）

| 项 | 取值 |
|---|---|
| 程序集 | `PuddingDiagnostics`（`PuddingCode.Diagnostics` 命名空间族） |
| 依赖 | `ProjectReference = 0`、`PackageReference = 0`；只用 BCL |
| 允许的 BCL 面 | 含 `System.Net.Http` / `System.Net.Sockets` / `System.Security.Authentication` —— 分类器必须识别 `HttpRequestException`、`SocketException`、`HttpIOException`、`AuthenticationException` |
| 禁止 | 任何 Pudding 程序集、Serilog、EF Core、ASP.NET、DI 容器、测试框架；不读配置、不写文件/库/日志、不建连接 |
| 边界判据 | `PuddingDiagnosticsTests/ComponentBoundaryTests`（探测器自检 + csproj 反读 + 源码/程序集/依赖闭包三类扫描）；临时加反向引用即取红 |

## 2. 关键概念

| 概念 | 权威位置 | 要点 / 不变量 |
|---|---|---|
| 稳定因果码 | `Contracts/DiagnosticCauseCode.cs` | 取值是跨层契约（日志/元数据/终态 DTO/前端本地化共用）；新增码必须同时补目录条目与「场景 → 期望码」用例 |
| 码表目录 | `Contracts/DiagnosticCause.cs`（`DiagnosticCauseCatalog`） | 码 → 可重试性 + 中文用户消息 + 处置建议；未登记码（如 `vision.*` 透传）走诚实兜底，不猜语义 |
| 失败阶段 | `Contracts/DiagnosticPhaseKind.cs` | Build/Serialize/RequestUpload/AwaitResponseHeaders/ReadResponseStream/Persist/Queued；线路字符串由用例锁死 |
| 因果分类 | `Classification/LlmFailureClassifier.cs` | 纯函数；判定顺序 透传码 → 取消 → 看门狗超时 → HTTP/传输/协议 → 诚实兜底；`Retryable` 已结合「是否产出增量」（Docs/08 §7.9） |
| 异常链与帧指纹 | `Classification/ExceptionChainInspector.cs` | 展开内因链、取 socket 错误码 / `HttpRequestError`；`DefaultUploadFrames` 是 2026-10-07 实测帧名（有断言锚定） |
| 有界证据 | `Contracts/DiagnosticEvidence.cs` | ≤32 键、单值 ≤512 字符、总量 ≤8 KiB；触顶写 `evidence_truncated=true`；键与输出 ordinal 排序 |
| 写入期脱敏 | `Redaction/DiagnosticRedactor.cs` | 键名**子串**匹配（修既有整串相等的穿透）、值形态检查、URL query 剥离；所有证据值必经此处 |
| 事故投影 | `Projection/IncidentProjector.cs` + `Projection/DiagnosticFact.cs` | 事实 → `IncidentView`（尝试明细 / 根因 / 阶段 / 体积 / 结论句 / 证据定位）；无证据时报 unknown 而不是 healthy |
| 查询契约 | `Query/DiagnosticFactQuery.cs` | 过滤维度 + 分页 + **显式截断标记**（修既有「Limit 静默 clamp 后当时间窗结论」） |
| 界面呈现 | `Presentation/ErrorPresentation.cs` | 标题/大概原因/严重度/可重试/建议动作/原码；标题禁出现异常类型名与英文原文，未知码原样显示 |
| 复制现场 | `Presentation/DiagnosticReportDocument.cs` | 版本化 schema + 捕获时间 + errorId/traceId/turnId/sessionId + 因果码/阶段 + 尝试明细 + 证据（含异常链）+ 日志定位提示；`RenderText()` 人读、`RenderJson()` 机器读；64 KiB 显式截断 |
| 故障场景资产 | `Scenarios/FaultScenarios.cs` | 「注入的失败 → 期望结论」对照表；新增码缺场景即取红 |

## 3. 关键文件

| 文件 | 用途 |
|---|---|
| `PuddingDiagnostics.csproj` | 叶子工程（0 引用）；注释写清与 `PuddingVectorIndex` 不同的边界理由 |
| `Contracts/DiagnosticCause.cs` | `DiagnosticCause` / `DiagnosticCauseDescriptor` / `DiagnosticCauseCatalog` |
| `Contracts/DiagnosticContext.cs` | 分类器输入（提供者/模型/尝试/阶段提示/耗时/体积/取消与超时标志/透传码） |
| `Contracts/IDiagnosticCauseClassifier.cs` | 分类器契约（实现必须是纯函数） |
| `Classification/LlmFailureClassifier.cs` | LLM 调用链分类器（生产用 `Default`，测试可注入帧指纹） |
| `Redaction/DiagnosticRedactor.cs` · `Contracts/DiagnosticEvidence.cs` | 脱敏与证据预算 |
| `Projection/*` · `Query/*` · `Scenarios/*` · `Presentation/*` | 事故投影、查询契约、故障场景表、界面呈现与复制现场载荷 |

## 4. 测试

| 工程 | 覆盖 |
|---|---|
| `Source/PuddingDiagnosticsTests/` | 30 例：场景对照表全量、事故复刻（含证据字段）、指纹锚定、无阶段信号时的诚实兜底、增量后禁止重试、码表漂移、脱敏与证据预算、事故投影、界面呈现与复制现场（标题友好性/未知码原码/定位四件套/JSON 稳定性/截断/不泄露密钥）、S1–S4 边界断言（含探测器自检与取红） |

## 5. 消费方（接入进度）

| 消费方 | 状态 |
|---|---|
| `Source/PuddingCore` 网关族（openai/responses/anthropic） | 待接入（设计文档 §7 Stage 3） |
| `Source/PuddingRuntime`（`DirectLlmClient` / `AgentExecutionService`） | 待接入（Stage 3） |
| `Source/PuddingPlatform`（事实落库/投影/API/诊断包） | 待接入（Stage 4） |
| `Source/PuddingHost`（组合根装配、日志路由） | 待接入（Stage 3/4） |
| `Source/PuddingPlatformAdmin`（按码本地化） | 待接入（Stage 4） |
