# 技能组合盘点（G1 · 只读）

生成时间：2026-09-21 18:30:11 +08:00
技能根目录：D:\data\agents\default.global_general-assistant.6a8\skills

> 只读产出：未写任何技能文件、未改 manifest、未动 enabled 状态。
> 关键词口径**逐字对齐既有实现** CollectKeywords（`Source/PuddingRuntime/Services/Skills/SkillEnforcerService.cs:126`）：
> `Keywords → Tags → SkillId → Name → Name 分词`，全部 `OrdinalIgnoreCase` 去重。

## 1. 存量

| 指标 | 值 |
|------|----|
| 技能总数 | 144 |
| 启用 | 139 |
| 禁用 | 5 |
| 关键词槽位（启用技能，含重复，小写归一后计） | 2490 |
| 去重后关键词 | 755 |
| 重复占用比（槽位中属于「第二次及以后」声明的比例） | 69.7% |

## 2. 关键词空间：噪声占比

| 类别 | 判定规则 | 槽位（全部技能） | 占比 |
|------|----------|------------------|------|
| D1 治理/溯源标签 | 来自 manifest.tags（精确分类） | 824 | 32.1% |
| D2 工具名 | snake_case 形态 + 显式裸名清单 | 957 | 37.3% |
| D3 名称分词 | Name 切分出的片段 | 502 | 19.6% |
| 语义关键词 | 显式 keywords / Name 全句 / SkillId | 280 | 10.9% |

**结论**：2563 个关键词槽位中 **2283 个（89.1%）** 与「技能教了什么」无关（D1+D2+D3）。

### 2.1 占用最多的单个关键词

| 关键词 | 类别 | 声明的技能数 |
|--------|------|--------------|
| self-evolution | D1 | 139 |
| auto-generated | D1 | 138 |
| source-session:206a9b48ec904ebb93e7541131fbb835 | D1 | 100 |
| self-evaluated:1.0.1 | D1 | 92 |
| file_read | D2 | 62 |
| dedup-reviewed:1.0.1 | D1 | 55 |
| save_memory | D2 | 53 |
| shell | D2 | 49 |
| query_sub_agents | D2 | 48 |
| search_grep | D2 | 46 |
| goal_read | D2 | 43 |
| terminal_wait | D2 | 42 |
| git_status | D2 | 42 |
| git_commit | D2 | 39 |
| goal_update | D2 | 39 |

## 3. 关键词冲突：被静默挤掉的注入机会

**结构根因**（既有实现，非本报告推测）：`SkillEnforcerService.GetOrRefreshKeywordMapAsync` 建映射时
`if (!map.ContainsKey(kw)) map[kw] = entry.SkillId;` 是**先到先得** —— 同一关键词被多个技能声明时，
只有索引顺序里的第一个能通过该关键词被注入，其余**静默失去这次注入机会**（技能仍启用，效果被抵消）。

| 指标 | 值 |
|------|----|
| 被 ≥2 个启用技能共享的关键词 | 165 / 755 |
| **被挤掉的注入机会总数**（Σ 声明数-1） | **1735** |

> 表中"实际命中者"按 **skillId 序**取第一个 —— 这是**近似**：真实顺序由 SkillEnforcer 的索引构造顺序决定，
> 本报告不读取运行时索引，故不声称精确。但**被挤掉的机会数**与顺序无关，是确定值。

| 共享关键词 | 类别 | 声明数 | 实际命中者（近似） | 被挤掉 |
|------------|------|--------|--------------------|--------|
| self-evolution | D1 | 134 | adjudicate-parallel-subagent-work-in-progress | 133 |
| auto-generated | D1 | 133 | adjudicate-parallel-subagent-work-in-progress | 132 |
| source-session:206a9b48ec904ebb93e7541131fbb835 | D1 | 100 | adjudicate-parallel-subagent-work-in-progress | 99 |
| self-evaluated:1.0.1 | D1 | 92 | adjudicate-parallel-subagent-work-in-progress | 91 |
| file_read | D2 | 62 | async-sub-agent-delegation-with-status-polling | 61 |
| dedup-reviewed:1.0.1 | D1 | 55 | adjudicate-parallel-subagent-work-in-progress | 54 |
| save_memory | D2 | 53 | adjudicate-parallel-subagent-work-in-progress | 52 |
| shell | D2 | 49 | async-dotnet-test-run-and-log-inspection | 48 |
| query_sub_agents | D2 | 48 | adjudicate-parallel-subagent-work-in-progress | 47 |
| search_grep | D2 | 46 | adjudicate-parallel-subagent-work-in-progress | 45 |
| git_status | D2 | 42 | agent-repo-health-check | 41 |
| terminal_wait | D2 | 42 | adjudicate-parallel-subagent-work-in-progress | 41 |
| goal_read | D2 | 42 | agent-state-reconciliation-task-board-sync | 41 |
| spawn_sub_agent | D2 | 39 | agent-state-reconciliation-task-board-sync | 38 |
| goal_update | D2 | 39 | async-dotnet-test-run-and-log-inspection | 38 |

## 4. 家族分布（按语义关键词聚簇）

聚簇规则：两个启用技能共享 ≥1 个**语义类**关键词即同族（并查集）。
刻意排除 D1/D2/D3 —— 它们几乎人人都有，算进去会退化成"一个大家族"的假象，所以并列给出含噪声口径作对照。

| 口径 | 家族数 | 最大族 | 成员 ≥2 的族 | 孤立技能（族大小=1） |
|------|--------|--------|--------------|----------------------|
| 仅语义关键词 | 139 | 1 | 0 | 139 |
| 含全部关键词（噪声危害对照） | 2 | 138 | 1 | 1 |

### 4.1 最大的 10 个语义家族

| 族内技能数 | 成员 |
|------------|------|
| 1 | read-only-code-diagnosis |
| 1 | verify-feature-commit-merge-status-in-mainline |
| 1 | read-only-subtask-contract-scaffolding |
| 1 | read-agent-task-board |
| 1 | requirement-intake-to-task-with-recon-and-memory |
| 1 | grounded-code-patch-with-di-verification |
| 1 | async-dotnet-test-run-and-log-inspection |
| 1 | resolve-llm-route-before-subagent-spawn |
| 1 | task-board-state-inspection-with-acceptance-criteria |
| 1 | bounded-goal-evidence-refinement |

## 5. 成本：索引投影 与 注入体量

| 指标 | 值 | 说明 |
|------|----|------|
| 索引投影字符数（启用技能） | 45941 | name + tags + keywords 拼接，**确定值** |
| 索引投影 token 估算 | ≈ 11519 | 公式 ASCII/4 + 非ASCII/1，**估算值** |
| 全量 SKILL.md 字节（启用技能） | 667681 | 若全部注入的体量上界 |
| 平均单技能 SKILL.md 字节 | 4803 | 单次注入的典型量级 |

**体量最大的 10 个启用技能**（单次注入成本最高者）：

| 技能 | 字节 | 行数 | 关键词槽位 |
|------|------|------|------------|
| trace-server-authoritative-flag-source-to-runtime | 15902 | 193 | 11 |
| status-card-redesign-with-contract-tests | 15018 | 275 | 16 |
| deployed-feature-acceptance-audit | 13829 | 181 | 19 |
| sqlite-readonly-baseline-probe | 11845 | 255 | 12 |
| oversized-goalmd-archival-and-slim-down | 11456 | 99 | 23 |
| verify-deployed-build-freshness | 10995 | 224 | 11 |
| grounded-code-patch-with-di-verification | 10614 | 112 | 18 |
| chat-bundle-slimming-remediation | 10452 | 132 | 19 |
| subagent-artifact-integration-with-provenance-commit-and-memory | 9692 | 132 | 22 |
| utf8-db-export-to-file | 9530 | 187 | 16 |

## 6. 淘汰候选**代理指标**（不是判决）

这些是**代理指标，不是判决**：本报告不使用遥测（谁真正被命中、命中后是否有用），
只能指出"命中能力最弱"的技能，**不能**据此淘汰任何技能。真正的淘汰判据要等 G2 使用遥测。

| 代理指标 | 技能数 | 含义 |
|----------|--------|------|
| 零语义关键词 | 0 | 只能靠工具名/标签/名字分词命中 ⇒ 不是可复用的程序知识 |
| 语义关键词 ≤ 1 | 50 | 命中面极窄 ⇒ 与噪声无法区分 |

## 7. 方法与边界（读报告时必看）

- **数据源**：每技能目录的 `manifest.json`（skillId/name/tags/keywords/enabled）与 `SKILL.md` 字节数。
- **关键词口径**：逐字复现 `CollectKeywords`（含 Tags 与 Name 分词、OrdinalIgnoreCase 去重）。
- **D2 判定**：snake_case 形态规则 + 显式补充的裸名工具清单（shell/sleep/asr）；可能有遗漏 ⇒ D2 只低估不高估。
- **确定值**：存量、分类、共享统计、被挤掉机会数、字节数、字符数 —— 均可由本脚本复现。
- **估算项**：token 数（公式 ASCII/4 + 非ASCII/1）。真实值取决于 tokenizer，**不得**当实测值引用。
- **近似项**："实际命中者"按 skillId 序取第一；真实顺序由运行时索引构造决定。被挤掉的机会数不受此影响。
- **未覆盖**：不读取运行时日志 ⇒ 不含"实际注入次数 / 命中次数"（那是 G2 使用遥测的范围）。
- **不改状态**：全程只读；运行前后技能文件与 enabled 状态必须逐字节一致。
