# Goodput SLO — Step 4 Test Checkpoint（checkpoint-v1）

- goalRunId `tg-9ec9d27543747ec6849a52aae10d9f31`｜task `0b16740022f84b58a9532a87f1bc5509`｜stepNode `tn-3ae79b18b206b68dfd2e46397644c27b`（**Test**, seq 4/5）
- step objective 逐字：*"Run focused verification and capture reproducible evidence and failures."*
- 上轮 verdict：`blocked / contract_coverage_insufficient`（`unmetCriteria` 空）；step1–3 已通过（`stepsPassed = 3/5`）

---

## 1. 测试策略：**对抗性验证**（不是把上一步的命令再跑一遍）
目标是打自己：找出刚交付的 P1 实现里**会把"缺数据"当成"节省"**的地方。

## 2. 三个缺陷（全部由本轮测试/实测发现，并已修复 + 固化为回归测试）
### F1 空洞真值（我的实现缺陷）
旧式 `SavingsClaimable => Totals.ZeroCostWithTokensRows == 0`。当某 Goal 的所有迭代都落在扫描窗口之外（**无任何归因行**）时，`0 == 0` ⇒ **返回 true** —— 即"缺数据"被当成"节省"，恰是卡片点名要禁止的行为。
- 修复：`SavingsClaimable => Totals.UsageRows > 0 && Totals.ZeroCostWithTokensRows == 0`
- 并新增显式字段 `IterationsWithoutUsage`，使"无窗口内用量行的迭代数"**可见**而非静默为零。
- 回归测试：`GetGoalReportAsync_NoUsageRowsInWindow_DoesNotClaimSavings`

### F2 过度归因不可见（实测发现）
Goodput 卡 **iter1（settled）**：按 TraceId 归因 prompt = **3,589,053**，而 `goal_iterations.input_tokens` = **1,195,063**（约 3×）⇒ **一个 TraceId 可跨多个迭代**。
- 修复：每 trace 暴露 `RecordedPromptTokens` / `RecordedCompletionTokens`（来自结算记账）与三态 `LedgerConsistency`；不一致时明确为 `mismatch`，**不静默取信**。
- 回归测试：`GetGoalReportAsync_TraceAcrossMultipleIterations_ExposesRecordedLedgerMismatch`

### F3 在飞迭代误报（实测发现）
**iter4** 状态 `accepted`、`iter_input_tokens = 0`（尚未结算），但归因出 19 行 / prompt 3,287,911 ⇒ 布尔一致性标志会对**在飞迭代**误报"不一致"。
- 修复：三态 `agrees` / `mismatch` / `not_comparable`；非终态（非 settled/failed/cancelled）一律 `not_comparable`。
- 回归测试：`GetGoalReportAsync_UnsettledIteration_IsNotComparableWithRecordedLedger`

## 3. **我自己的失误与一次误判（如实记录）**
- 第 1、2 次定向运行均 `exit_code=1`。我初判为"预期失败被捕获" —— **这是错的**：日志里**没有任何测试摘要**（`grep '失败! -'` 无命中），实为**构建失败**。
- 根因：我的 `file_patch` 把锚点行 `private static DateTimeOffset OccurredAt` 放进 `old_text`，却**未在 `new_text` 中放回** ⇒ 等于**删除**了该属性。
- 教训（已内化）：`file_patch` 的替换是"整段覆盖"，**锚点行必须完整回写**。
- 连带后果：**F1 的"红态"未被真实捕获**（我在修复构建错误前就已应用了 F1 修复）。F1 的存在性由**代码逻辑推演**（空集 ⇒ `0 == 0` ⇒ `true`）+ 现回归测试守护，**不是**由一次真实的红→绿过程证明。此点如实标注，不夸大证据强度。

## 4. 逐字证据（外部可核查）
| 运行 | 逐字结果 | exit_code |
|---|---|---|
focused #1 | 日志**无测试摘要**（构建失败，原因见 §3） | 1 |
focused #2 | 同上（构建错误未修复） | 1 |
focused #3 | `已通过! - 失败:     0，通过:    16，已跳过:     0，总计:    16，持续时间: 2 s - PuddingPlatformTests.dll (net10.0)` | **0** |
全工程回归 | `已通过! - 失败:     0，通过:  1340，已跳过:     0，总计:  1340，持续时间: 1 m 12 s - PuddingPlatformTests.dll (net10.0)` | **0** |

⇒ 基线 1324 + 新增 16 = **1340**，完全吻合；测试数从 11 → 16。

## 5. 交叉验证：本轮新增 4 个迭代级样本，双源**逐字一致**
| 样本 | 归因 prompt | 结算 `input_tokens` | 结论 |
|---|---|---|---|
路由卡 iter5 | 1,902,856 | 1,902,856 | agrees ✓ |
路由卡 iter6 | 597,776 | 597,776 | agrees ✓ |
Goodput iter2 | 2,764,810 | 2,764,810 | agrees ✓ |
Goodput iter3 | 2,473,353 | 2,473,353 | agrees ✓ |
Goodput iter1 | 3,589,053 | 1,195,063 | **mismatch（F2 来源）** |
Goodput iter4 | 3,287,911 | 0（未结算） | **not_comparable（F3 来源）** |

⇒ 累计 **8/12** 个迭代样本双源逐字一致，2 个 mismatch、1 个 not_comparable（另 1 个为窗口外样本）。
**新增边界测试**：单段 SourceId（`context_compaction`）⇒ 未解析；`round=0` ⇒ 接受。

## 6. 未覆盖 / 未证明
- **接线后行为**（服务仍未接线，见 step3 §7）—— 三态与 `SavingsClaimable` 的**生产**效果未验证。
- P2（缺价标记）/P3（心跳投影）/P4（时间账）/P5（三项 SLO 口径）**均未开始**。
- 全部证据为**本机单库单次**运行；生产长期运行、多机、并发未验证。
- `mismatch` 的**归因**（是"trace 跨迭代"还是"实现过度归因"）**尚未定性**，只是把它变成可见事实。

## 7. 下一步
P1b 接线（DI + `agent_diagnostics` 动作 + 重启核验）→ **复跑本套 16 个测试** → 再按 P2 → P3 → P4 → P5 推进。
风险：R1 接线需在无活跃 Goal 窗口重启 Core；R3 本卡 plan_version 须保持稳定（否则触发缺陷 `5413ce1b`）。
