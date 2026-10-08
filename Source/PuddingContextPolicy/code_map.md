# PuddingContextPolicy · code_map

> 组件化交付规程（`Docs/10_conventions/组件化交付规程.md`）状态：**S1–S4 已交付，S5 未接入**。
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
| `ContextPolicyContracts.cs` | 输入/输出合同：消息策略视图、容量输入/结果、压力分类、来源常量、策略版本 | `ContextMessageShape`、`ContextCapacityInputs`、`ContextCapacityResult`、`ContextPressureDecision`、`ContextEffectiveWindowSources`、`ContextPolicyVersion` | 只带整数与**稳定身份**，不带正文；来源取值与 Runtime 的 `ContextEffectiveWindowSources` 逐字对齐 |
| `ContextCapacityArithmetic.cs` | 容量算术与压力分类 | `ContextCapacityArithmetic.Resolve/Classify` | `effectiveInput = min(providerInputLimit, modelWindow − reservedOutput − safetyBuffer)`；回退预留必须记为 `fallback_output_reserve`，不得冒充用户预算；**先判硬边界再判软阈值** |
| `ContextCandidateSelector.cs` | 候选边界选择与稳定指纹 | `ContextCandidateSelector.Select/ComputeFingerprint`、`CandidateSelectionOptions`、`CandidateSelectionResult` | 前导 System 不可移除；最后 N 条与当前用户轮不可移除；只按完整会话单元移除（不拆 tool call 与 result）；`ProjectedUsedTokensExcludingSummary` **不是**提交后的值 |
| `ContextCandidateApplicability.cs` | 严格适用校验（旧候选能否提交） | `ContextCandidateApplicability.Validate`、`FrozenCandidateDescriptor`、`CurrentContextDescriptor`、`CandidateApplicabilityVerdict` | 代次/revision/路由/工具/策略版本任一变化 ⇒ `stale_candidate`；保留后缀必须与当前历史**逐位一致**（新消息追加/中间插入/删改一律失效）；不做尾部拼接或自动 rebase |
| `ContextNetGainAdmission.cs` | 净收益准入 | `ContextNetGainAdmission.Evaluate`、`NetGainInputs`、`NetGainDecision` | 必须用**真实摘要长度**重算 after；摘要为空/不缩小 ⇒ `no_gain`；加摘要后仍越界 ⇒ 不得提交；未达目标允许提交但必须如实记录 |
| `ContextBackoffPolicy.cs` | 退避/去重判定 | `ContextBackoffPolicy.Decide`、`CandidateMaintenanceRecord`、`CandidateMaintenanceOutcomes` | **硬保护优先于任何退避**；同候选同代次上的 `applied` 记录不可信（成功应用应推进代次） |

## 3. 不变量（改这个组件前先读）

1. **软/硬分离**：软维护可延期，硬保护不可被任何准入/退避/冷却跳过（ADR-095 D1/D2）。
2. **身份而非正文**：指纹、适用性校验都只吃稳定身份与版本号 —— 组件不应看到 Prompt 正文。
3. **after 必须是真实值**：计划阶段「移除后、未加摘要」的估算不得进入日志/事件充当 after。
4. **旧候选绝不覆盖新消息**：保留后缀逐位不变才允许提交。
5. **回退值不冒充用户预算**：`fallback_output_reserve` 与 `output_reserve` 是两个来源。
6. **只依赖 BCL**：任何 `ProjectReference` / `PackageReference` 都是边界破裂（S4 会取红）。

## 4. 测试与门禁

| 位置 | 覆盖 |
|---|---|
| `Source/PuddingContextPolicyTests/ContextCapacityArithmeticTests.cs` | 容量算式与来源；**2026-10-07 事故两条样本的记录值回放**（583,691 判软 / 625,824 判硬，上限 605,760，软阈值 484,608） |
| `…/ContextCandidateSelectorTests.cs` | 触发门槛、完整单元移除、当前用户轮保护、tool 调用与结果不拆分、指纹确定性与版本敏感性 |
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

## 5. 尚未接入（S5）

未登记 `PuddingAgentNetwork.slnx`、未在任何消费方 csproj 加引用、未在 DI 组合根装配。
接入前须先完成 S5 的四步与宿主组合测试；**S5 之前不得改动宿主**。
