# 技能内容审计（只读）

生成时间：2026-09-21 18:39:53 +08:00
技能根目录：D:\data\agents\default.global_general-assistant.6a8\skills

> 只读产出。本报告回答「技能有没有用」里**能靠静态内容回答**的那一半。
> **不能**回答的一半：技能是否真的被命中、命中后是否让任务更好 —— 那必须有使用遥测（当前不存在）。

## 1. 这些技能是怎么来的

| 指标 | 值 | 占总启用技能 |
|------|----|--------------|
| 启用技能总数 | 139 | 100% |
| 标记 auto-generated | 133 | 95.7% |
| 正文含 ## 来源 段（会话回放式） | 135 | 97.1% |
| 可提取到置信度 | 134 | 平均 81.1% |

## 2. 有没有边界知识（最关键）

边界知识 = 前提 / 约束 / 不适用场景 / 陷阱 / 回滚。这些是**模型最缺、也最容易被过时技能误导**的部分：
一条只讲「怎么做」的技能，对强模型是冗余；一条讲错「什么时候不该做」的技能，是**负价值**。

| 指标 | 值 |
|------|----|
| 提到边界知识的技能 | 11（7.9%） |
| **纯回放**（有来源段 + 零边界知识） | **125（89.9%）** |

缺边界知识的技能（最多列 20 个）：

- adjudicate-parallel-subagent-work-in-progress
- agent-recent-activity-check
- agent-repo-health-check
- agent-state-reconciliation-task-board-sync
- agent-status-health-check
- analyze-ui-screenshot-with-vision-tool
- async-dotnet-test-run-and-log-inspection
- async-sub-agent-delegation-with-memory-checkpointing
- async-sub-agent-delegation-with-monitoring
- async-sub-agent-delegation-with-status-polling
- async-terminal-execution-with-output-polling
- atomic-delegation-discipline
- authorized-redacted-config-audit
- batch-commit-and-push-dirty-workspace-by-logical-groups
- blocking-terminal-job-wait
- board-progress-audit-and-delegated-fix
- bounded-goal-evidence-refinement
- build-test-verify-commit-push-pipeline
- bundle-budget-byte-attribution-probe
- chat-bundle-slimming-remediation

## 3. 正文有多少是「调哪几个工具」

形如 ``N. `tool_name``` 的行 = 工具调用序列。这部分是**模型本来就会的**（工具描述已包含用法）：

| 指标 | 值 |
|------|----|
| 工具调用行数 | 721 |
| 正文总行数 | 10334 |
| 占比 | 7% |

## 4. 体量分布

| 指标 | 字节 |
|------|------|
| 最小 | 0 |
| 中位 | 4261 |
| 平均 | 4803 |
| 最大 | 15902 |

## 5. 版本分布（用户提到的「仅适用某版本」在数据里的影子）

| 版本 | 技能数 |
|------|--------|
| 1.0.0 | 28 |
| 1.0.1 | 92 |
| 1.0.2 | 4 |
| 1.0.3 | 7 |
| 1.0.4 | 5 |
| 1.0.5 | 1 |
| 1.1.0 | 1 |
| 2.0.0 | 1 |

## 6. 这张表能证明什么、不能证明什么

**能证明**：这批技能的来源与形态高度同质（自动提炼、会话回放式、以步骤为主）；
对「模型已经会做」的部分做了大量抄录，而**边界知识（何时不该用）覆盖很低**。

**不能证明**：它们**没有用**。没有使用遥测，就无法区分三种情况：
① 确实被命中且帮上了（应保留）；② 从未被命中（应淘汰或重建触发面）；
③ 被命中但把模型带偏（**负价值**，最危险）。本报告无法区分 ①②③。

⇒ 这正是「索引 + 调用次数 + 成功率」遥测基础设施要解决的问题。
