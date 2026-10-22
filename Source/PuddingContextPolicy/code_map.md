# PuddingContextPolicy · code_map

> 组件化交付规程（`Docs/10_conventions/组件化交付规程.md`）状态：**S1–S5 已交付**（已登记解决方案，
> 并已在主机侧被消费；见 §5）。
> 边界硬规则：本组件**不得**引用 `PuddingCore` / `PuddingRuntime` / `PuddingMemoryEngine` /
> `PuddingHost` / `PuddingAgent` / `PuddingPlatform` 或任何 LLM SDK；判据是**编译期失败**，
> 并由 `Source/PuddingContextPolicyTests/ComponentBoundaryTests.cs` 在运行期复核（S4）。

## 1. 这个组件是什么

上下文**容量与压缩候选**的纯策略库：只做可独立测试的判定与算术，不做任何 I/O。
它把「该不该压缩、压哪一段、压完是否真的获益、旧候选还能不能用、要不要退避」这些判断
从 Runtime 的历史读写/锁/网络/事件里抽出来，使它们可以脱离宿主测试（规程 R1/R4）。

**不负责**：计时器、数据库、网络、Session 锁、后台任务、生命周期事件发布、Prompt 组装。
输入是**最小不可变 DTO**（只有整数与稳定身份，没有宿主类型），Runtime 在边界处转换。

## 2. 文件索引

| 文件 | 职责 | 关键符号 | 约束 |
|---|---|---|---|
| `PrefixCompactionPolicy.cs` | 前缀缓存友好的分阶段调度与追加式候选校验 | `Decide/ValidateAppendOnly` | 可配置比例乘注册模型窗口；默认 60% 准备、65% 提交就绪候选、70% 等待；上传轴独立等待阈值 95%；预测及实际输入上限优先；冻结历史逐位不变才允许追加 |
| `ContextPolicyContracts.cs` | 输入/输出合同：消息策略视图、容量输入/结果、压力分类、来源常量、策略版本 | `ContextMessageShape` 等 6 型合同（消息/容量输入输出/压力/来源/版本）（见下「关键语义」） | 只带整数与**稳定身份**，不带正文；来源取值与 Runtime 的 `ContextEffectiveWindowSources` 逐字对齐 |
| `ContextCapacityArithmetic.cs` | 容量算术与压力分类 | `ContextCapacityArithmetic.Resolve/Classify` | `effectiveInput = min(providerInputLimit, modelWindow − reservedOutput − safetyBuffer)`；回退预留必须记为 `fallback_output_reserve`，不得冒充用户预算；**先判硬边界再判软阈值** |
| `ContextCandidateSelector.cs` | 候选边界选择与稳定指纹 | `ContextCandidateSelector.Select/ComputeFingerprint` + 选项/结果两型（见下「关键语义」） | 前导 System 不可移除；当前 User、System、最新完整执行单元及未完成工具批次不可移除；近期 N 条偏好在硬压力下受预算约束；完整历史轮与轮内已完成执行单元可选；token/字节预算包含摘要与包装预留；`ProjectedUsedTokensExcludingSummary` **不是**提交后的值 |
| `ContextCandidateApplicability.cs` | 严格适用校验（旧候选能否提交） | `ContextCandidateApplicability.Validate` + 冻结/当前描述符与判定（见下「关键语义」） | 代次/revision/路由/工具/策略版本任一变化 ⇒ `stale_candidate`；保留后缀必须与当前历史**逐位一致**（新消息追加/中间插入/删改一律失效）；不做尾部拼接或自动 rebase |
| `ContextNetGainAdmission.cs` | 净收益准入 | `ContextNetGainAdmission.Evaluate`、`NetGainInputs`、`NetGainDecision` | 必须用**真实摘要长度**重算 after；摘要为空/不缩小 ⇒ `no_gain`；加摘要后仍越界 ⇒ 不得提交；未达目标允许提交但必须如实记录 |
| `ContextBackoffPolicy.cs` | 退避/去重判定 | `ContextBackoffPolicy.Decide`、`CandidateMaintenanceRecord`、`CandidateMaintenanceOutcomes` | **硬保护优先于任何退避**；同候选同代次上的 `applied` 记录不可信（成功应用应推进代次） |

**关键语义（§2「文件索引」表·关键符号列逐字原文）**

> 上表超长单元格已缩为语义摘要并以 `（见下「关键语义」）` 指回本段；下列为**逐字原文**（行序与上表一致），折行为软换行、字面不增删。

- `ContextPolicyContracts.cs`
  `ContextMessageShape`、`ContextCapacityInputs`、`ContextCapacityResult`、`ContextPressureDecision`、`ContextEffectiveWindowSources`、`ContextPolicyVersion`
- `ContextCandidateSelector.cs`
  `ContextCandidateSelector.Select/ComputeFingerprint`、`CandidateSelectionOptions`、`CandidateSelectionResult`
- `ContextCandidateApplicability.cs`
  `ContextCandidateApplicability.Validate`、`FrozenCandidateDescriptor`、`CurrentContextDescriptor`、`CandidateApplicabilityVerdict`

## 3. 不变量（改这个组件前先读）

1. **软/硬分离**：软维护可延期，硬保护不可被任何准入/退避/冷却跳过（ADR-095 D1/D2）。
2. **身份而非正文**：指纹、适用性校验都只吃稳定身份与版本号 —— 组件不应看到 Prompt 正文。
3. **after 必须是真实值**：计划阶段「移除后、未加摘要」的估算不得进入日志/事件充当 after。
4. **旧候选绝不覆盖新消息**：严格校验要求后缀不变；显式追加模式要求整个冻结历史逐位不变，并由调用方保留追加尾部、重新计量和执行持久 CAS。
5. **回退值不冒充用户预算**：`fallback_output_reserve` 与 `output_reserve` 是两个来源。
6. **只依赖 BCL**：任何 `ProjectReference` / `PackageReference` 都是边界破裂（S4 会取红）。

## 4. 测试与门禁

| 位置 | 覆盖 |
|---|---|
| `Source/PuddingContextPolicyTests/ContextCapacityArithmeticTests.cs` | 容量算式与来源；**2026-10-07 事故两条样本的记录值回放**（583,691 判软 / 625,824 判硬，上限 605,760，软阈值 484,608） |
| `…/ContextCandidateSelectorTests.cs` | 触发门槛、完整单元移除、当前用户输入保护、tool 调用与结果不拆分、指纹确定性与版本敏感性 |
| `…/CompletedExecutionCompactionTests.cs` | 长轮内执行跨度、双预算、并行/未完成工具批次、冻结位置消除重复正文歧义 |
| `…/ContextCandidateApplicabilityTests.cs` | 未变化可提交；**新消息追加即失效**；中间插入/删改失效；五类版本变化各自 `stale_candidate` |
| `…/ContextNetGainAdmissionTests.cs` | 真实摘要重算 after（事故口径 583,691−257,299+4,343=330,735）、空/不缩小 ⇒ no_gain、仍越界 ⇒ 拒绝、未达目标如实记录 |
| `…/ContextBackoffPolicyTests.cs` | 硬保护覆盖退避、同候选退避、间隔到期、新候选/新代次可再评估、`applied` 无代次推进不抑制 |
| `…/ComponentBoundaryTests.cs` | S4 边界断言：检测器自检（控制组）、进程未加载上层/重依赖、声明依赖闭包、组件元数据引用仅 BCL |

无宿主验证（S3）：`dotnet build Source/PuddingContextPolicy/PuddingContextPolicy.csproj` 与
`dotnet test Source/PuddingContextPolicyTests/PuddingContextPolicyTests.csproj` 都可以在
Core/Desktop 运行中执行，不碰宿主文件锁。

**取红证据（S4 可证伪性）**：临时给测试工程加一条 `ProjectReference` 指向 `PuddingRuntime` ⇒
`Test_Process_Must_Not_Load_Forbidden_Assemblies` 与
`Test_Dependency_Closure_Must_Not_Contain_Forbidden_Assemblies` 两条**变红**；删除后 csproj
`SHA256` 逐位复原、42/42 全绿。

## 5. 接入状态（S5）

- **已登记解决方案**：`PuddingAgentNetwork.slnx` 含 `Source/PuddingContextPolicy/PuddingContextPolicy.csproj`
  与 `Source/PuddingContextPolicyTests/PuddingContextPolicyTests.csproj`。
- **已被消费**：`Source/PuddingRuntime/PuddingRuntime.csproj` 通过 `ProjectReference` 引用本组件；
  Runtime 在 warm-prefix 规划边界消费 `ContextCandidateSelector`（完整跨度及双预算），提交边界消费 `ContextCandidateApplicability`（版本/身份适用性）与
  `ContextNetGainAdmission`（收益与硬上限准入）。
- **无需 DI 登记**：本组件只有静态纯策略（无状态、无 I/O），因此没有服务注册项；
  这不等于「未接入」。
- 组件自身仍**只依赖 BCL**；边界由编译期强制 + `ComponentBoundaryTests` 运行期复核。
