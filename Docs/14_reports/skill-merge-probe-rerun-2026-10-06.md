---
title: 技能合并可行性探针复跑：era 子集代理腐化，两条门禁用例永久红（2026-10-06）
slug: skill-merge-probe-rerun-2026-10-06
related_files: [Source/PuddingRuntimeTests/Services/SkillMergeEligibilityRealIndexProbeTests.cs, Docs/14_reports/skill-near-duplicate-2026-09-21.md, Docs/12_features/RSI-G4-任务书-2026-09-22.md, Source/PuddingMemoryEngine/Services/SkillCurationGate.cs]
---

# 技能合并可行性探针复跑（2026-10-06）

## 0. 为什么要跑这一刀

原计划是"对 219 条技能池做一次近重复审计并给出合并/退休清单"。**动手前先做轮子调研**，
发现平台**已有一等实现**，于是改为**复跑既有仪器**而非新写一套：

| 既有件 | 位置 | 性质 |
|---|---|---|
| 家族聚类 | `SkillFamilyClusterer` + `SkillFamilyPolicy` | 名称 token Jaccard ≥ 0.25 |
| 合并四条件判据 | `SkillMergeEligibilityJudge` + `SkillMergePolicy` | 纯判据、零 IO |
| 既有确定性闸门 | `SkillEvolutionDeduplicationService.IsDeterministicallyEligible` | 生产路径上那条 |
| **只读探针（本次跑的）** | `Source/PuddingRuntimeTests/Services/SkillMergeEligibilityRealIndexProbeTests.cs` | 读 `manifest.json` + `SKILL.md`，不写盘、不调 LLM |
| 整理轨（报告型） | `SubconsciousWorkerService.SkillCurateAsync` + `SkillCurationGate`(C1–C5) | **家族超限 ⇒ 只出审查请求，绝不写盘** |

## 1. 复跑方式（可复现）

```
门控变量：PUDDING_G4_DATA_ROOT=D:\Data
          PUDDING_G4_AGENT_ID=default.global_general-assistant.6a8
命令：    powershell -NoProfile -ExecutionPolicy Bypass -File temp\g4-probe.ps1
脚本要点：DOTNET_CLI_UI_LANGUAGE=en-US；--filter FullyQualifiedName~SkillMergeEligibilityRealIndexProbeTests
          --logger "console;verbosity=detailed"；Tee 到 temp\g4-probe.log
终态：    G4PROBE_EXIT=1（Total tests: 3 / Passed: 1 / Failed: 2）
```

## 2. 实测输出（原文，未改写）

```
families(≥2)=10 members=33 largest=11 sizes=[11, 5, 3, 2, 2, 2, 2, 2, 2, 2]
   11  async-sub-agent-delegation-with-memory-checkpointing
    5  agent-state-reconciliation-task-board-sync
    3  agent-recent-activity-check

era-subset: skills=214 enabled=209 families=10 intraFamilyPairs=75
text>=0.20: pairs=75 fourCondition=16 gateOverlay=0 medianTextSim=0.198 failed[ProceduralTextSimilarity=39 MergeableEvidence=47]

Assert.AreEqual failed. Expected:<144>. Actual:<214>.   （EraBaselineSkillCount）
Assert.AreEqual failed. Expected:<17>.  Actual:<16>.    （FourConditionBaseline020）
```

## 3. 判读（按测试类自带的分诊纪律）

测试类自己写死了分诊顺序：**① 先判口径漂移 ② 再判数据变化**——"若 §3-F 时代子集仍能逐数复现
144/139/10/33/11/75，则口径未漂移、差异来自技能仓增长；若子集复现不了，就是口径漂移 ⇒ 修实现"。

| 数字 | 基线（09-21） | 本次 | 判读 |
|---|---|---|---|
| 家族数 / 成员数 / 最大簇 / 同族对 | 10 / 33 / 11 / 75 | **10 / 33 / 11 / 75** | **逐数复现** ⇒ 聚类口径与家族结构**未变** |
| 四条件通过（0.20 档） | 17 | **16** | 数据变化（median 文本相似度 **0.198**，紧贴阈值） |
| 叠加既有闸门（0.20/0.35/0.50） | 0 / 0 / 0 | **0 / 0 / 0** | **裁决不变** |
| era 子集技能数 / 启用数 | 144 / 139 | **214 / 209** | **仪器腐化**（下节） |

⇒ 结论一：**"四条件判据在真实语料上不构成已生效的合并能力"这一裁决仍成立**（叠加面恒为 0）。
⇒ 结论二：近重复的**人工审查面**是稳定的 10 家族 / 33 技能 / 75 对；其中 0.20 档仅 16 对过四条件。
⇒ 结论三（本轮新发现，见 §4）：**两条 era 用例已永久红**，而且是"仪器坏了"，不是"结论变了"。

## 4. 新发现：era 子集代理机制腐化（门禁失效）

**现象**：`eraSubset.Count` 期望 144，实测 **214**（池子 219，只按硬编码的 **5 个技能名**排除）。
⇒ "时代子集"实际 = "全池 − 5"，早已不是 2026-09-21 的快照。

**为什么不能照纪律"更新基线"了事**（这是本次最需要说清的一点）：

1. **名字清单无法随增长维护**：每加一个技能就要改一次清单，否则该用例恒红——而这正是它的现状。
2. **文件系统时间也不能当时代代理**：实测技能目录的 `LastWriteTimeUtc` 被**批量重写**
   （绝大多数为 `2026-09-30T19:14:38Z`，一次 bulk 操作），所以"按目录创建时间切时代"同样不可靠。
   （这多半正是当初选用名字清单的原因——但代价清单本身不可维护。）
3. 于是"时代子集"这个概念在当前技能仓上**没有可靠载体**，继续保留只会训练人们忽略红灯。

**建议（最小改动，未实施）**：
- 保留 `RealSkills_ShouldSatisfyMergeProbeInvariants`（其注释明确"不会随数据增长而变砖"——它是**结构性不变式**，包括"叠加面 ⊆ 四条件面"这条能取红的关键不变式）；
- 两条 era 用例降级为**报告型**（打印数字 + 断言结构性不变式 + 不 assert 与日期绑定的绝对计数），或直接 retire 并在文档留指针；
- 若仍需历史锚点，把 144/139/10/33/11/75 与 17/4/0 作为**带日期的观测记录**留在文档（本文件即承接该角色），而不是留在会变红的断言里。

## 5. 本轮未做（边界）

- ⛔ 未修改任何源码、测试或断言；⛔ 未删/禁任何技能；⛔ 未跑任何写操作；⛔ 未 push。
- 未跑 `SkillCurateAsync`（它需要在 Worker 编排内触发；本轮只确认它是**报告型**：`..._NeverWrite` 系列用例）。

## 6. 影响

- 面板/工具侧**不受影响**：`SkillCurate` 是报告型，本裁决不改变其"超限 ⇒ 出审查请求"的行为。
- "合并"这条路**当前没有自动化空间**（叠加面 0）；可行的只有**人工审查 10 家族 / 75 对**，
  且必须先解决"用哪份判据给审查人排序"的问题（现状：median 0.198，紧贴 0.20，阈值不稳健）。

## 7. 追加发现：家族评审路径在生产上**从未被喂过策略**（机制休眠，且是设计使然）

### 7.1 证据链

| # | 事实 | 位置 |
|---|---|---|
| 1 | `if (familyPolicy is null \|\| portfolioPolicy is null) return (0, 0);`，注释明示这是"尾随可选参数默认 `null` ⇒ 零回归"的实现点 | `Source/PuddingMemoryEngine/Services/SubconsciousOrchestrator.cs:1823,1827` |
| 2 | 生产调用点 **①** 不传这两个策略：`SkillCurateAsync(workspaceId, agentInstanceId, memoryLlmConfig, ct: ct)` | `Source/PuddingRuntime/Tools/BuiltIns/Management/SubconsciousTriggerTool.cs:152` |
| 3 | 生产调用点 **②** 同样不传 | `Source/PuddingRuntime/Services/Background/SubconsciousWorkerService.cs:280` |
| 4 | 全仓检索 `SkillFamilyPolicy.Create` / `SkillPortfolioPolicy.Create` / `new SkillFamilyPolicy` **仅命中测试**（`SubconsciousWorkerServiceTests`、`SubconsciousTriggerToolTests`）⇒ 生产侧无策略来源 | ⚠️ 该检索 `coverage: partial`（scanned 856/2000）⇒ **强证据，非完全证明** |
| 5 | 手动触发路径的响应体**不含**该事实：只返回 `action/duration_ms/n_before/n_after/not_reduced_reason/candidate_count/retire_suggestion_count/summary`。只有 Worker 的 `metadata["family_review_count"]` 带它 | `SubconsciousTriggerTool.RunSkillCurateAsync` vs `SubconsciousWorkerService` |

**净效果**：`SkillCurationReport.FamilyReviewCount ≡ 0`；`notReducedReason` 里
"Additionally N family merge review request(s) …" 那段分支**永不执行**；系统对外报"未发现冗余"，
而**家族上限检查根本没跑**。这与本项目一直在修的同类毛病同源：**把"未知"报成"正常"**。

### 7.2 为什么这不是"改一行就能修"的 bug（关键）

`SkillCurationPolicy` 的类型文档**刻意拒绝提供默认值**：

> `MinRetainedValueRatio` 的下限**没有**可论证的默认值……**禁止**写一个"看起来合理"的数字进产品；
> `Create` 的所有旋钮都必须由调用方显式给出。

而同源规则（探针注释所引任务书 §2.5-3）又明确：

> **禁止静默保留一个永不触发的判据。**

⇒ 现状**同时**踩中后者（判据永不触发）与前者（阈值不可由编码者自定）：
编码者既不能让它继续静默、也不能自己填数。**这是一处决策缺口，不是编码缺口。**

### 7.3 需要人工裁决的两个量（即当前阻塞）

| 需要裁决 | 载体 | 说明 |
|---|---|---|
| 家族内上限 | `SkillPortfolioPolicy.PerFamilyCap` | 测试里用过 `1` 作为**探针值**，⛔ 不是产品结论 |
| 合并后价值保留比例下限 | `SkillCurationPolicy.MinRetainedValueRatio` | 类型文档明确"下限是治理结论，需人工裁决" |

决策所需语料（本轮实测）：**10 家族 / 33 成员 / 最大簇 11**，
最大簇 = `async-sub-agent-delegation-with-memory-checkpointing`（11 条）。

### 7.4 本轮未做（边界）

- ⛔ 未新增任何"默认策略"（那正是类型文档禁止的）；⛔ 未改任何调用点
  ——因为唯一正确的方向是"把人工裁决的结果接进去"，而不是"由编码者猜一个数"。
- ⛔ 未跑 `SkillCurateAsync`（只做静态取证 + 既有探针复跑）。
