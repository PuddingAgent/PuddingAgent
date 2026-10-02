# 技能近重复检测（只读）

生成时间：2026-09-21 18:44:00 +08:00

> 目的：用**名称分词 token 的 Jaccard 重叠**找「多条技能在讲同一件事」。
> ⚠️ 故意**不**使用 keywords/tags：它们含大量共享工具名（如 `file_read`），会让所有技能都显得相似。
> 因此本指标是**保守的下界**：它只能发现「名字就像同一件事」的重复，发现不了措辞完全不同的同类。

## 1. 三档阈值下的近重复簇

| Jaccard 阀值 | 簇数(≥2) | 涉及技能数 | 最大簇 |
|--------------|-----------|------------|--------|
| ≥ 0.25 | 10 | 33 | 11 |
| ≥ 0.4 | 6 | 14 | 4 |
| ≥ 0.6 | 1 | 2 | 2 |

⇒ 在**最宽松**的 0.25 档下，139 个启用技能中有 **33 个（23.7%）落在近重复簇里**。

## 2. 规模 ≥ 3 的簇（强证据，说明这些主题上确实重复建设）

-（11 条）
  - async-sub-agent-delegation-with-memory-checkpointing
  - async-sub-agent-delegation-with-monitoring
  - async-sub-agent-delegation-with-status-polling
  - async-terminal-execution-with-output-polling
  - milestone-handoff-with-collaborator-notification-and-memory-persistence
  - parallel-full-stack-feature-implementation-via-async-sub-agent-delegation
  - review-and-commit-changes-with-detailed-message
  - sub-agent-delegation-with-verification-and-memory-persistence
  - subagent-artifact-integration-with-provenance-commit-and-memory
  - verified-atomic-sub-agent-delegation
  - verify-build-then-commit-and-push-code-changes
-（5 条）
  - agent-state-reconciliation-task-board-sync
  - chat-panel-task-board-management
  - paged-task-board-snapshot-audit
  - read-agent-task-board
  - task-board-state-inspection-with-acceptance-criteria
-（3 条）
  - agent-recent-activity-check
  - agent-repo-health-check
  - agent-status-health-check

## 3. 最大 15 个簇（按规模降序）

-（11 条）async-sub-agent-delegation-with-memory-checkpointing | async-sub-agent-delegation-with-monitoring | async-sub-agent-delegation-with-status-polling | async-terminal-execution-with-output-polling | milestone-handoff-with-collaborator-notification-and-memory-persistence | parallel-full-stack-feature-implementation-via-async-sub-agent-delegation | review-and-commit-changes-with-detailed-message | sub-agent-delegation-with-verification-and-memory-persistence | subagent-artifact-integration-with-provenance-commit-and-memory | verified-atomic-sub-agent-delegation | verify-build-then-commit-and-push-code-changes
-（5 条）agent-state-reconciliation-task-board-sync | chat-panel-task-board-management | paged-task-board-snapshot-audit | read-agent-task-board | task-board-state-inspection-with-acceptance-criteria
-（3 条）agent-recent-activity-check | agent-repo-health-check | agent-status-health-check
-（2 条）triage-test-failures-as-pre-existing-via-git-history | verify-task-completion-via-git-history
-（2 条）codebase-investigation-and-pre-change-audit | codebase-semantics-audit-before-refactor
-（2 条）task-context-reconnaissance | task-context-retrieval-for-follow-up-inquiries
-（2 条）dual-persist-to-memory-and-goal | persist-runtime-discovered-constraints-and-behavior-switches-to-memory
-（2 条）persist-user-decision-as-backlog-task-with-context | persist-user-preference-with-resource-verification
-（2 条）generate-and-send-image-to-conversation | generate-styled-image-with-reference-and-send-to-chat
-（2 条）git-repository-overview-and-path-discovery | inspect-git-repository-overview

## 4. 对 G1 结论的修正

G1 曾得出「**139 个技能语义层面两两零重叠**」，那是按**关键词身份**聚簇得到的结论：
它只能看到「两个技能声明了同一个关键词」，看不到「两个技能在用不同措辞讲同一件事」。
本报告改用名称 token 重叠后，**同一批数据**下出现了明显近重复簇（见 §2）。

⇒ 正确表述是：
- 关键词层面：无重叠（因为工具名/标签被大量争抢，重合都发生在噪声上）；
- **语义层面：存在近重复**（本报告 §2 就是证据）。
- 而两者之间的空白（真正的语义相似度）需要 **embedding** 才能量准 —— 那正是 T4 切片。

## 5. 对 RSI 的意义

这组簇给 RSI 提供了第一个**不依赖使用遥测**的负信号判据形态：
`redundant_with`（与另一技能近重复）—— 注意它是**关系**而非布尔值，
因为「重复」本身不直接等于「该删」（取决于哪一条有真实使用记录，而那要等遥测）。
