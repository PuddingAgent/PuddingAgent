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
