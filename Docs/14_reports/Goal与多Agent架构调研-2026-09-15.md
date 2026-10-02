# Goal 机制与多 Agent 架构调研（2026-09-15）

> 目的：为 ADR-092（两级校验 + 受控 `IGoalCheckRunner` + 统一结算）与 G92-1 提供外部对照，
> 判断哪些设计值得借鉴、哪些差异必须保留。
> 方法：由研究子代理（`smart_research`）抓取官方文档/开源仓库为主，二手来源逐条标注。
> **置信度说明**：以下要点中，标记「官方」者为直接抓取的一手文档；标记「二手」者为转述；
> 报告的完整正文（含证据行号）落在本会话 artifact：
> `.pudding/context-tool-results/206a9b48ec904ebb93e7541131fbb835/call_00_p2AP0Y4FC4rrDvpjotCl3576-smart_research.txt`。
> 该文件为单行转义 JSON（约 262KB），中段无法用行式工具读取，§2.5–§2.7 与 §3 的部分结论
> 系按同一批一手来源重建（语义一致，非逐字引用）。

## 1. zcode（ZCode，官方 https://zcode.z.ai/cn/docs/goal ）

- **一个会话同时只有一个目标**；命令集 `/goal`（查看）、`/goal <目标>`（设定，已有目标时替换）、
  `/goal replace`、`/goal pause`、`/goal resume`、`/goal clear`。[官方]
- **可校验性是第一等要求**：目标越具体越可校验，每轮判断越准。正例「把 `pnpm test` 跑通，
  并且首屏加载控制在 2 秒内」；反例「优化一下性能」。适合"一句话说得清、但要多轮才能做完"的任务。[官方]
- **状态**：推进中 / 已暂停 / 已完成 / 已清除。暂停"不会丢东西"（已跑轮次、工具调用记录、
  产出文件都在）。**没有独立的"失败"终态**：失败表现为"没达成 → 给出下一步 → 自动开下一轮"。[官方]
- **谁判定完成**：每轮结束后由**系统单独做一次校验**，不是执行模型在对话里自行宣布；校验失败会产出
  "下一步该做什么"并自动开下一轮，用户不需要追问"继续"。[官方]
- **证据白名单**：改出来的文件 / 命令输出 / 测试结果；**过程的努力量不算证据**。[官方]
- **终止条件三选一**：校验判定完成 / 用户主动暂停或清除 / 跑到 per-goal 的用量上限。[官方]
- **文档层面空白**：未提及幂等键、去重、fencing/epoch、重复结算防护，也未定义重试次数上限、
  同一失败重复检测、replan 策略（"文档未提及 ≠ 未实现"，但对我们是可直接借鉴的空白区）。[官方文档范围]

## 2. 其他 harness 的 goal/plan 设计

| | 谁执行验证 | 防"声称通过" | 持久化/恢复 | 终止条件 |
|---|---|---|---|---|
| **Claude Code** | 模型自己跑测试 + 读 diff；人类用 plan 审批与权限模式把关；**无内置独立 verifier** | 流程纪律：「task list is not proof by itself」，只有验收条件被真实检查过才可置 `completed`；VERIFY 阶段要求"带证据才标完成"；Plan mode 是只读 permission mode；Explore 子代理只读 | `TaskCreate/Update/Get/List`（替代 `TodoWrite`），`pending→in_progress→completed`；子代理在独立上下文窗口、有独立工具白名单与权限 | 子代理级 `maxTurns`；主循环靠 plan 审批 + 权限门；未见全局轮次/预算终止 |
| **Cursor** | 云端 VM 内 agent 自己构建、测试、操作浏览器；本地 Plan mode 由人类审批方案 | "proposes an approach **without touching your files**"；官方明确"能写代码但不能跑测试的 agent 无法闭环"；hooks（preToolUse/beforeShellExecution/afterFileEdit…）承载格式化与策略检查；结果以 PR 交给人工 | 隔离 VM + 环境快照与 build 版本可追溯；multi-repo worktree | run 结束推送分支开 PR；首次使用要求设置 spend limit |
| **Devin** | Devin 自己在云 VM 里测自己的改动（起应用、点击、确认改动生效）；另有 Devin Review 做审查并**自动修复每条 finding 直到 diff 干净** | 进入测试前先写 **test plan**，必须"grounded in source, not assumptions"；**执行动作前先标注期望行为**（先承诺，事后难把意外说成通过）；交付物是具名截图/带 assertion 列表的 test report | **Snapshots** + YAML blueprint 声明式环境；session 可被事件/定时/其他 Devin 异步触发 | 输出报告 + verdict；多条本质不同路径都失败则如实报"未能验证"；2.1 版给 confidence 🟢🟡🔴 |
| **OpenHands** | **分层验证**：Layer 1 critic（打分）→ Layer 2 code review + QA；critic 打分驱动早停/重试；高风险转人工 | 10 项审查清单 + 🟢🟡🔴 verdict；QA 四阶段；有 give-up 路径 | SDK 事件流（event stream）作为可回放事实；skills 扩展挂 review/qa | 早停/重试 + verdict + give-up |

[上表 §2.1–§2.4 为官方来源为主；各条 URL 见 artifact EVIDENCE]

**其余（二手或仅取到锚点，置信度较低）**
- **Codex CLI**：Thread / Turn / Item 三层，`turn/completed` 事件，审批即暂停，`/responses/compact` 压缩，前缀缓存。[二手]
- **SWE-agent**：强调 ACI（agent-computer interface）设计——linter 门、100 行 file viewer、空输出话术。[官方仓库为主]
- **Aider**：architect/editor 双模型分工、`/ask` `/code`、edit formats；atomic Git commit。[部分二手]

## 3. Hermes Agent（多 Agent）要点

- **委派原语**：`delegate_task`（隔离子代理）+ group 语义；子代理有独立上下文。[来源：hermes-agent 文档/仓库，含二手]
- **投递语义**：**at-least-once + ack**（任务投递需要确认，允许重复投递 → 需要幂等消费）。[官方仓库]
- **并发上限**：文档写 10 并发；issue 与媒体报道为 3（来源冲突，未定论）。[冲突]
- **预算**：`IterationBudget` 父 90 / 子 50 **各自独立**，支持 `consume`/`refund`（`execute_code` 会退还），**每 turn 重置**。[社区深潜文章，二手]
- **角色与流程**：存在 **IMPLEMENT-THEN-VERIFY** 的角色分离提案（先实现、再由独立角色验证）、tier 路由、
  **no auto-retry**（失败不自动重试，交由上层决策）、`output_schema` 校验失败只做一次纠正。[二手/提案]

## 4. 对 ADR-092 / G92-1 的借鉴（可操作）

1. **"每轮结束由系统单独校验，而非执行模型自证"** 已被 zcode/Devin/OpenHands 共同印证 —— 我们的
   `ConservativeGoalIterationVerifier`（只读、不接受自然语言 DONE）+ 受控 `IGoalCheckRunner` 分层方向正确，
   应继续坚持"verifier 不执行工具、runner 不写终态"的职责分离。
2. **证据白名单式表述值得落到文案/契约里**：zcode 的"改出来的文件 / 命令输出 / 测试结果"与我们
   `ExpectedEvidence` + `ReportRef` + `InvocationId` 是同一意图；建议在条件合同里把"允许的证据类型"
   显式化（如 `evidence_kinds`），避免把"努力量/叙述"当证据。
3. **先承诺期望、再执行动作**（Devin 的"annotate expected behavior right before acting"）可低成本借鉴：
   我们的 `GoalCheckSpec.ExpectedTestCount` / `ExpectedEvidence` 已经是这个机制，建议在**报告**里回填
   "实际 vs 期望"的差异字段（`ExecutedTestCount` 已有，可补 `Expected` 快照以便审计）。
4. **分层验证**（OpenHands：critic → code review/QA）提示：除 build/test 外应支持 semantic/external 类检查
   分别归属不同执行者角色（我们已有 `ExecutorRole`，`GoalVerificationSpecKinds` 也留了 Semantic/External）。
5. **预算必须父子独立且可退还**（Hermes `IterationBudget`）：我们目前只有 `MaxIterations` 单一计数，
   建议在结算层区分"目标预算 / 单轮预算"，并让"等待（waiting）"不消耗迭代预算（否则 wait 会把预算烧光 →
   变成不可恢复失败）。
6. **投递 at-least-once + ack ⇒ 消费必须幂等**：我们的 outbox `(goalId, epoch, nextIteration)` 与
   结算幂等键 `(goalId, epoch, verificationId)`、检查去重键
   `(scope, criterionRevision, definitionHash, inputFingerprint)` 正对应这一条；检查执行器还需要
   **租约过期回收**（已实现 `GoalCheckRecordStore.LeaseAsync` 回收过期租约）。
7. **no auto-retry + 一次纠正**（Hermes）：与我们的 `ComputeDisposition` 保守策略一致——
   未知一律 `repair`，不默认完成；建议对"同一失败指纹重复 N 次"给出显式升级路径（如转 NeedsUser），
   避免 repair 死循环（zcode 只有用量上限，我们应比它更明确）。
8. **暂停不等于丢失**（zcode）与 **快照可复用**（Devin Snapshots）：我们的"等待不关闭 Goal 身份"方向正确；
   可借鉴的是把**执行环境指纹**（如 `CheckWorkingDirectory` + 依赖版本）记入报告，便于恢复后判断"旧绿灯是否仍有效"。

## 5. 未获取到 / 缺口

- OpenAI Codex agent-loop 官方博客 403；`code.claude.com/docs/en/plan-mode`、`cursor.com/docs/agent/plan`
  返回 404（已改用其他官方页补齐）。
- Devin 交互式规划页为 JS 渲染，正文未取到，改由三篇官方博客支撑。
- 对闭源产品"文档未提及 ≠ 未实现"；本报告对这类情形一律标注为"空白"而非"缺失"。
