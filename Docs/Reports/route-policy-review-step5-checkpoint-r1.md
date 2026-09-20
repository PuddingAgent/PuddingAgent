# 阶段感知模型路由 — Step 5 Review Checkpoint（checkpoint-v1）

- goalRunId `tg-0992b9b323e3c4e4a38f7fa51407f55d`｜iteration 6｜stepNode `tn-8dbf91fb1ed92ef468657d1b5ce9514b`（**Review**, seq 5/5）
- step objective 逐字：*"Review acceptance evidence, unresolved risk and completion proof."*
- 前置：step1–4 均通过（`progress.stepsPassed = 4/5`）

---

## 1. 证据台账（artifact × commit × 逐字证据）

| step | commit | 交付物 | 可核查证据 |
|---|---|---|---|
1 Explore | `58eca06d` | `Docs/Reports/route-policy-explore-checkpoint-step1-r1.md` | 修正卡片两处前提（taskType 确定性路由已存在、route snapshot 冻结已存在） |
2 Plan | `e286851f` | `Docs/Reports/route-policy-plan-freeze-step2-r1.md` | 现状三类化台账 + P1-1..P1-6 冻结路径 + 所有权边界 4 条 |
3 Change | `7cbd4162` | `ModelRoutePolicyContracts.cs`(130) + `ModelRoutePolicyEvaluator.cs`(262) + `ModelRoutePolicyEvaluatorTests.cs`(250) + `code_map.md` 2 行 | 逐字：`已通过! - 失败: 0，通过: 14，已跳过: 0，总计: 14，持续时间: 78 ms`，`exit_code=0` |
4 Test | `d5aeba8e` | 同测试文件补 3 测试（14→17） + `route-policy-test-step4-checkpoint-r1.md` | 逐字：`已通过! - 失败: 0，通过: 1324，已跳过: 0，总计: 1324，持续时间: 1 m 14 s`，`exit_code=0` |

## 2. 六条验收标准逐条裁决（**我的自述，终局由服务端 verifier 裁决**）

| 标准 | 裁决 | 依据与缺口 |
|---|---|---|
1 记录 taskType/phase/route/reason/quality floor/health snapshot | **部分达成（约 5/7 字段）** | `ModelRouteDecision` 已承载 taskType/phase/route(provider+model+protocol)/reason/quality floor；**health snapshot 缺**；**WorkUnit 级落库未接线**（决策不会被任何 WorkUnit 记录持有） |
2 不由自由文本决定 + 同 snapshot 确定性 | **部分达成** | 自由文本**类型层不可达**（`WorkUnitRouteContext` 无标题/描述）；确定性已由 3 类测试覆盖（同输入逐字相同、24 种排列不变、拒绝码顺序无关）。**但生产选择路径未接线** ⇒ "route 选择不由主模型自由文本决定"在生产上**尚未成立** |
3 401/403/model-unavailable/rate-limit 差异化策略 + ≤1 次 failover | **未达成** | 仅冻结在计划（P1-3），零实现 |
4 Agent/workspace/provider/model/token 四约束并发 | **未达成** | 仅 provider/model 槽位（既有 `ProviderRateLimiter`） |
5 ≥50 WorkUnit shadow A/B 成表 | **未达成** | 零实现；且无 shadow 落库面 ⇒ 连计数都无从开始 |
6 P95<30s + 无连续重复派发 | **未达成** | 数据源（`rate_limit_wait_ms`）已确认存在，但**落库表未定位** ⇒ 当前**不可度量** |

**综合**：6 条中 **0 条完整达成**、**2 条部分达成**、**4 条未达成**。

## 3. 对自己交付物的**对抗性审查**（本轮最有价值的部分）

### 3.1 实质批评①：`RoutePolicyCatalog` 的质量下限是**凭空常数**
`0.60 / 0.85 / 0.75 / 0.70 / 0.80` 是我按阶段语义**编造**的，没有任何实测依据。
而卡片明确要求「以**成功工作单元**做 A/B」来形成路由表 ⇒ **这些常数必须在 A/B 之后被替换**，
否则等于把一个未经证实的先验伪装成"策略"。**风险等级：高**（会静默影响真实选择）。

### 3.2 实质批评②：新增件目前是**死代码**
2 个源文件**没有任何生产消费者**。按工程纪律（不保留无用代码），这属于需要**立即收口**的状态：
要么**下一个迭代完成接线**，要么撤回。我选择保留的唯一理由是它们同时是 step2 冻结契约的载体且已被 17 个测试覆盖；
但**若下一迭代仍未接线，应当撤回而非长期悬挂**。

### 3.3 实质批评③：`Fingerprint` 的语义需要更精确的表述
当前指纹覆盖**输入**（策略 + 上下文 + 排序后的候选集），即它标识的是「决策上下文」而**不是决策本身**。
这符合 criterion 2（同 snapshot ⇒ 同结果），但我此前在 step3 文档中的措辞可能让人误读为"决策指纹"。
**此处更正**：`Fingerprint` = **输入快照摘要**；决策本身由 `Selected+Code+ProviderId+ModelId` 表达。
另：指纹会随**候选集变化**（如模型目录增删）而变化 —— 这是期望行为（输入变了），但意味着它**不能**作为跨时间的"决策稳定性"标识。

### 3.4 次要问题（已记录，不构成阻塞）
- `StableOrder` 用 `Normalize`（trim+lower）排序，`PreferenceOrder` 用 `OrdinalIgnoreCase` —— 两处确定性都成立，但**风格不一致**，建议统一。
- 拒绝码取"确定性遍历中最先失败者"：单候选场景唯一，多候选场景的语义应写进 XML 注释（目前只在文档里）。
- 新增测试带 1 条 `warning MSTEST0037`（与仓库既有 ≥6 处同类告警一致，未额外改动）。

## 4. 未解决风险（**必须随交付物传递**）

| # | 风险 | 后果 |
|---|---|---|
R1 | 本卡 plan `tp-cdaa80e3…` 的 `plan_version` 必须保持 = 2 | 任何 recompile 使其 ≥3 ⇒ 坠入缺陷 `5413ce1b` 的**永久死锁**（与 Scheduler 卡同源） |
R2 | `rate_limit_wait_ms` / `stream_first_chunk_wait_ms` 的**落库表未定位** | criterion 6 的 P95 与卡片 TTFT 软分**当前不可离线度量**（与 Tracker 卡标准 6 同类缺陷） |
R3 | WorkUnit 级 route 记录位**未确认**（`task_nodes` 能否承载七元组） | criterion 1 的实现形态未定 |
R4 | §3.1 的凭空质量下限 | 会把未证实先验伪装成策略，静默影响真实选择 |
R5 | §3.2 的死代码状态 | 若长期不接线，属违反工程纪律的悬挂改动 |
R6 | `lastVerdict` 连续多轮为 `contract_coverage_insufficient` | 见 §6 |

## 5. 完成证明（proven / not proven）

**已被证据支持（proven）**
- 新增确定性求值器在**纯函数层**满足确定性：同输入逐字相同、**全部 24 种候选排列**不变、拒绝码顺序无关（3 个独立测试）。
- 指纹对 9 个结构化字段**敏感**（防静默忽略）。
- 新增件**未破坏既有测试面**：`PuddingPlatformTests` **1324/1324 通过、0 失败、exit_code=0`**。
- 代码已提交并推送（`7cbd4162`、`d5aeba8e`）；`code_map.md` 已同步（经 `git_diff` 验证为纯插入）。

**未被证据支持（not proven）**
- 生产路由行为**有任何改变**（未接线）。
- criterion 3/4/5/6 的任何一条。
- 质量下限常数的**任何经验依据**。
- 「同一 snapshot ⇒ 确定性」在**真实 provider/模型目录**下的成立（仅同进程、同进程内输入）。

## 6. 关于 `meta.goal_contract_proposal` —— 第三次明确立场

本卡 `acceptanceContract.source = bounded_planning`，harness 每轮索要该信封。我**拒绝**，理由（不变）：
其 verification 形态为 `definitionRef=checks/text-assertion.md#equals` + `expectedText` **等于本轮最终输出**
⇒ 对上轮输出做等值断言**恒真**，属**自证式契约**，会把"我自己说通过"写进验收语义。
替代做法：**外部可核查证据**（commit sha + 逐字测试输出 + 可重放命令）。冲突已登记平台缺陷 `709bbf5e`。

## 7. 裁定（**我这一侧的结论**）

- **计划的 5 个 step 全部完成**（Explore → Plan → Change → Test → Review）。
- **本卡 6 条验收标准未完成**：0 完整 / 2 部分 / 4 未达成 ⇒ **不建议标记任务完成**。
- 建议后续实现顺序（每项一个原子迭代）：**①接线（shadow、只读）→ ②补 health snapshot 与 WorkUnit 落库 → ③标准 3 差异化故障策略 → ④标准 4 四约束并发 → ⑤shadow A/B（≥50 WorkUnit）→ ⑥定位 metric 落库表以度量 P95**。
- **终局由服务端 verifier 裁决**；本文自述仅为提案。

## 8. 诚实限定
- 本文全部基于**本轮及前四轮的实际产物与逐字命令输出**；未新增任何未经运行的推断。
- 「1324/1324」的证据面 = **进程内单元/契约层**；不含集成、不含真实 provider、不含部署后行为。
- 所有"未达成"均写明**未实现**（而非"未验证"）；所有"部分达成"均写明**缺哪一部分**。
