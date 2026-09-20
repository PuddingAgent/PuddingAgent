# 阶段感知模型路由 — Step 3 Change Checkpoint（checkpoint-v1）

- goalRunId `tg-0992b9b323e3c4e4a38f7fa51407f55d`｜iteration 4｜stepNode `tn-0c94c3c67f904a6f3810860d2e0609fa`（**Change**, seq 3/5）
- step objective 逐字：*"Apply the bounded implementation changes within the declared conflict scope."*
- 上轮 verdict：`outcome=blocked`、`blockerCode=**contract_coverage_insufficient**`、`unmetCriteria=[]`
  ⇒ 该阻塞的正确读法是「**只交文档、没有落代码，覆盖不了契约**」⇒ 本轮改为**真正落代码 + 测试证据**。

---

## 1. 交付物（**纯新增文件，零既有文件改写**）

| 文件 | 行数 | 作用 |
|---|---|---|
`Source/PuddingPlatform/Services/Scheduling/ModelRoutePolicyContracts.cs`（新增） | 130 | `RouteSelectionPreference`、`ModelCapabilityProfile`、`RoutePolicy`、`WorkUnitRouteContext`、`ModelRouteDecision` 契约 + `RoutePolicyCatalog` 阶段默认策略目录 |
`Source/PuddingPlatform/Services/Scheduling/ModelRoutePolicyEvaluator.cs`（新增） | 262 | 确定性求值器：硬门 + 软偏好排序 + 顺序无关 SHA-256 `Fingerprint` + 枚举化 `Reason` + 机器可读拒绝码 |
`Source/PuddingPlatformTests/Services/Scheduling/ModelRoutePolicyEvaluatorTests.cs`（新增） | 250 | **14 个契约测试** |

**冲突面声明**：本次不修改任何既有文件（新增类当前不被生产代码消费，属「已实现、未接线」），
因此对既有行为的影响面为 **0**；这也严格贴合 step objective 的 *bounded / declared conflict scope*。

## 2. 测试证据（**逐字**）

命令：
```
dotnet test Source/PuddingPlatformTests --filter "FullyQualifiedName~ModelRoutePolicyEvaluatorTests" -v q
```
结果（逐字）：
```
总共 1 个测试文件与指定模式相匹配。

已通过! - 失败:     0，通过:    14，已跳过:     0，总计:    14，持续时间: 78 ms - PuddingPlatformTests.dll (net10.0)
```
进程 `exit_code = 0`。

## 3. 实现的判定语义（可被测试逐条核验）

| 测试 | 断言内容 |
|---|---|
`Evaluate_SameInputTwice_ProducesIdenticalDecisionAndFingerprint` | 同一 snapshot ⇒ 决策与指纹逐字相同（criterion 2） |
`Fingerprint_IsIndependentOfCandidateOrder` | 候选顺序置换 ⇒ 指纹不变 |
`Fingerprint_DistinguishesRiskClassification` | `risk=high` 与 `risk=low` ⇒ 指纹必须不同（不同风险不得被静默等同） |
`Evaluate_TaskTypeOrPhaseMismatch_IsRejected` | 策略/上下文错配 ⇒ `policy_context_mismatch` |
`Evaluate_MissingCapability_ReturnsCapabilityMissingCode` | 缺能力 ⇒ `capability_missing:code` |
`Evaluate_ContextWindowTooSmall_ReturnsCode` | 上下文不足 ⇒ `context_window_too_small` |
`Evaluate_ToolProtocolUnsupported_ReturnsCode` | 工具协议缺失 ⇒ `tool_protocol_unsupported` |
`Evaluate_BelowQualityFloor_ReturnsCode` | 低于质量下限 ⇒ `quality_floor_not_met` |
`Evaluate_VerifierPhase_RequiresIsolatedReadOnlyRoute` | Verify 阶段必须选 `isolated-readonly`，否则 `security_tier_mismatch`（卡片「Verifier 固定只读隔离 route」） |
`Evaluate_PlanPhase_PrefersHighestQuality_AndExplorePrefersLowestCost` | Plan ⇒ 最高质量；Explore ⇒ 最低成本（卡片「Explore/triage 偏快低成本；Plan/high-risk review 偏高质量」） |
`Evaluate_PreferredProvider_WinsTiesBeforeSoftPreference` | 首选 provider 在软偏好之前生效 |
`Evaluate_NoEligibleCandidate_IsNotSelectedAndExplainsWhy` | 无候选通过 ⇒ **不静默回退**，返回拒绝码 + 理由 + 指纹 |
`Reason_IsComposedOfEnumeratedTokensOnly` | Reason 逐字等于枚举化 token 串（**「无解释模型选择」在结构上不可能**） |
`Catalog_VerifierAndToolPhases_DeclareTheirHardGates` | 阶段目录硬门声明 + 未知 phase 确定性回落 |

## 4. 与 6 条验收标准的关系（**严格区分已实现/未实现**）
| 标准 | 本轮 | 说明 |
|---|---|---|
1 记录 taskType/phase/route/**reason**/quality floor/health snapshot | **部分** | `ModelRouteDecision` 已承载 taskType/phase/route/reason/quality floor 与拒绝码；**health snapshot 未实现**；**WorkUnit 级落库未接线** |
2 选择不由自由文本决定；同 snapshot ⇒ 确定性相同 | **部分达成** | 自由文本在**类型层面不可达**（`WorkUnitRouteContext` 无标题/描述字段）；同 snapshot 逐字相同有测试；**但尚未接入生产选择路径** |
3 401/403/model-unavailable/rate-limit 差异化策略 + 最多一次 failover | **未实现** | 仅冻结在 step2 计划（P1-3） |
4 Agent/workspace/provider/model/token 四约束并发 | **未实现** | 既有 `ProviderRateLimiter` 只覆盖 provider/model 槽位 |
5 ≥50 WorkUnit shadow A/B 成表 | **未实现** | 需 shadow 标记与统计，属 P1-5 |
6 容量等待 P95<30s + 无连续重复派发 | **未实现** | 数据源（`rate_limit_wait_ms`）已确认存在，**落库位仍未定位** |

## 5. 工程笔记（诚实记录）
- **本轮踩的坑**：首次编译失败 `ModelRoutePolicyEvaluator.cs(163,16): error CS0019 运算符"=="无法应用于"方法组"和"int"` ——
  根因是 `IEnumerable<string>.Count` 解析为 LINQ 方法组而非属性；修法物化 `.ToList()`。
- **工具链坑**：`dotnet ... | Select-String ...` 在本 shell 直接 `exit 255`（编码乱码）⇒ **禁止给 dotnet 输出接 `Select-String`**；
  改用 `-v q` 后读输出尾部。
- `code_map.md` 已按纪律补齐 2 行（`Services/Scheduling/` 索引表）。
- 新增测试文件带 1 条 `warning MSTEST0037`（`Assert.IsTrue` 建议改 `Assert.AreEqual`）——
  仓库内同类既有告警 ≥6 处，故与现状保持一致，未做额外风格改动。

## 6. 诚实限定（**必须保留**）
- 「已通过 14/14」只证明**该求值器的契约行为**，**不证明**生产路由已被改变 —— 新增件**尚未接线**。
- criterion 2 的「不由主模型自由文本决定」在**类型层面**成立，但**生产调用链是否改为使用本求值器尚未发生**。
- 标准 3/4/5/6 **全部未实现**，不进任何验收结论。
- 终局由服务端 verifier 裁决；本文自述仅为提案。
