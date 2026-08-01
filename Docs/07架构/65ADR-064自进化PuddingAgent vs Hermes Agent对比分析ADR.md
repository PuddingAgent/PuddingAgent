# ADR-064: 自进化 PuddingAgent vs Hermes Agent 对比与落地

| 属性 | 值 |
|------|-----|
| **编号** | ADR-064（当前目录另有同号 ADR，保留既有文件名；后续文档治理应统一重编号） |
| **日期** | 2026-07-30 |
| **状态** | 已实现 |
| **作者** | 通用助手 |
| **关联** | ADR-027、ADR-048、ADR-056、ADR-057、ADR-059 |

## 决策摘要

PuddingAgent 的“仅 Skill”自进化采用以下闭环：

> 成功工具轨迹 → 可复用性与安全门禁 → Agent 私有 `SKILL.md` + `manifest.json` + `index.json` → `SkillEnforcer` 自动加载 → 基于完整 Skill 内容进行后续改进。

模型选择采用“语义角色 → Agent 配置的模型绑定”，不采用“任务名称 → 硬编码模型”。主代理或
`smart_develop` 等语义工具只声明 `conscious`、`subconscious`、`developer` 等角色；执行边界再从
持久 Agent 实例解析 provider/profile/model。当前默认 Agent 的 `conscious` 为 DeepSeek V4 Pro，
`subconscious` 为 DeepSeek V4 Flash，但这是配置事实，不是代码分支。

周期任务不直接调用写操作，而是先进入 `ISubconsciousJobQueue`，再由 `SubconsciousJobScheduler` 与 `SubconsciousWorkerService` 统一租约、重试和完成。主宿主通过 `Subconscious:EnableWorker=true` 显式启用 Worker；该开关与调度参数统一绑定到从 `AppContext.BaseDirectory` 加载的 `bootstrapConfiguration`，确保 `dev-up.py` 从仓库根目录启动已编译 DLL 时仍读取部署目录中的 `appsettings.json`。

## Hermes 参考边界

Hermes Agent 主仓库支持 Agent 通过 `skill_manage` 创建、修改和删除 Skill，并把 Skill 作为跨会话程序性记忆。其独立的 `hermes-agent-self-evolution` 仓库才包含 DSPy + GEPA 的进化搜索；本 ADR 不引入种群、变异或多代选择。

PuddingAgent 借鉴的是“经验沉淀为 Skill，并在未来会话复用”的产品闭环，不复制 Hermes 的具体实现，也不把两个项目描述为完全等价。

## 实现后的端到端流程

```text
ChatExecutionCommand(status=succeeded)
  + canonical conversation_events(tool.call.requested/completed)
  + ChatMessage(user goal)
        │ workspaceId + agentInstanceId 隔离
        ▼
ConversationSkillEvolutionTrajectorySource
  ├─ 只读取成功 Command
  ├─ 要求至少 2 个已配对且 exitCode=0 的工具调用
  └─ 任一 tool.call.failed / 非零 exitCode / 未配对事件均拒绝
        ▼
SubconsciousOrchestrator.ExtractPatternsAsync
  ├─ 已有 Skill 的 source-turn 证据去重，已处理轨迹不再重复调用 LLM
  ├─ LLM 判断任务是否可复用
  ├─ passing_check / reusable / safe 三项必须全部通过
  └─ Flash 语义准入：create / merge / skip / defer；低置信度自动 defer
        ▼
AgentSkillEvolutionStore → AgentSkillFileService
  ├─ data/agents/{agentInstanceId}/skills/{skillId}/SKILL.md
  ├─ data/agents/{agentInstanceId}/skills/{skillId}/manifest.json
  └─ data/agents/{agentInstanceId}/skills/index.json
        ▼
SkillEnforcerService
  └─ 下一次用户消息命中 Keywords/Tags/Name 时注入 SKILL.md
```

### 自动改进

`ImproveSkillsAsync(workspaceId, agentInstanceId)` 首先由 Flash 对该 Agent 已启用的 `auto-generated` Skills 做语义聚类，再用确定性门禁复核工具指纹、文本相似度与来源证据。只有置信度至少 0.92 且两层判断一致时才合并；规范 Skill 吸收关键词和来源证据，重复项写入 `superseded-by:{skillId}` 并设为 `Enabled=false`，不物理删除，因此可以恢复。随后只枚举仍启用的 Skills，读取完整 `SKILL.md` 交给 LLM 评估。只有明确存在步骤矛盾、缺少验证或过时内容时才写回；写回使用同一个 `AgentSkillFileService.UpdateAsync`，同时更新 Markdown、manifest、索引和 patch 版本号。

该过程是无人审批的自值守闭环。模型响应无效、目标不存在、置信度不足或确定性门禁不通过时，系统自动 `defer`/跳过本轮写入并等待后续周期；不会要求人工确认，也不会在不确定时猜测或删除 Skill。每次有效去重与质量评估会写入带当前版本的 `dedup-reviewed:*` / `self-evaluated:*` 水位；内容版本未变化时后续周期不再重复调用 Flash，只有新建、合并或版本变化才重新审查。

旧实现把 Skill 写入 Memory Library 的“技能”Book，并且改进时只把标题元数据交给 LLM。该路径已删除，不再保留双写或兼容层。

## 周期调度

| JobType | 首次延迟 | 周期 | 执行入口 |
|---------|:--------:|:----:|----------|
| `memory.auto_dream` | 5 分钟 | 6 小时 | `AutoDreamAsync` |
| `skill.extract_patterns` | 10 分钟 | 12 小时 | `ExtractPatternsAsync` |
| `skill.improve` | 15 分钟 | 4 小时 | `ImproveSkillsAsync` |

三个定时循环只负责入队。幂等键包含 `jobType + workspaceId + agentInstanceId + 时间桶`，因此同一时间桶内多次唤醒或多实例竞争不会重复创建待执行任务。实际写操作由持久队列租约串行化。

主宿主当前配置：

```json
{
  "Subconscious": {
    "EnableWorker": true,
    "Scheduling": {
      "PeriodicJobsEnabled": true,
      "DefaultWorkspaceId": "default",
      "DefaultAgentInstanceId": "default.global_general-assistant.6a8"
    }
  }
}
```

延迟与周期均可通过 `Subconscious:Scheduling` 的 `*InitialDelaySeconds` / `*IntervalSeconds` 覆盖。

## 主动调试触发

受认证保护的调试 API 可以绕过首次延迟和周期等待，但不会绕过持久队列、Worker、租约、重试或质量门禁：

```http
POST /api/debug/subconscious/evolution/trigger
Authorization: Bearer <admin-jwt>
Content-Type: application/json

{
  "action": "all",
  "workspaceId": "default",
  "agentInstanceId": "default.global_general-assistant.6a8",
  "requestId": "manual-evolution-test-001"
}
```

`action` 支持 `auto_dream`、`extract_patterns`、`improve_skills` 和 `all`，省略时默认为 `all`。`workspaceId` 与 `agentInstanceId` 省略时读取 `Subconscious:Scheduling` 默认值。

接口返回 `202 Accepted`，响应中的 `jobs[]` 包含 `jobId`、`jobType`、`status`、`idempotencyKey` 与 `reused`。相同 `requestId` 重试时返回原作业，不会重新打开已完成作业。结果继续使用现有接口查询：

```text
GET /api/debug/subconscious/jobs/lookup?jobId={jobId}
GET /api/debug/subconscious/jobs/{jobId}/result
```

Worker 会在完成周期自进化 Job 前持久化 Orchestrator Report，因此结果接口对三类任务分别返回：

| action | result kind | 关键 Metadata |
|--------|-------------|---------------|
| `auto_dream` | `memory.auto_dream.v1` | suggested/executed/merged/archived/deleted count |
| `extract_patterns` | `skill.pattern_extraction.v1` | candidates/promoted/merged/deferred/demoted/skipped count、created/updated Skill IDs |
| `improve_skills` | `skill.improvement.v1` | evaluated/patched/consolidated/skipped count、improved/disabled duplicate Skill IDs |

三者成功结果均使用 `status=completed`、`decision=execution_completed`、`nextAction=complete_job`。即使本轮没有候选或无需修改，也会保存零操作 Report，避免“Job completed 但结果接口 404”。

该 API 仍受 `[Authorize]` 与 `Subconscious:DebugApiEnabled` 双重约束。

## 与原提案的关键修正

| 原判断 | 代码审计结果 | 本次决策 |
|--------|--------------|----------|
| 只缺 `Task.WhenAll` | 主宿主默认未注册 Worker | 配置显式启用 Hosted Service |
| `appsettings.json` 写成 `true` 即会生效 | `dev-up.py` 的进程工作目录是仓库根，`builder.Configuration` 不读取 DLL 目录配置 | Hosted Service 条件注册与 Options 绑定统一读取 `bootstrapConfiguration` |
| `ExtractPatternsAsync` 已生成运行时 Skill | 实际写入 Memory Library Book | 改为 `AgentSkillFileService` 目录型存储 |
| `ImproveSkillsAsync` 会修补已有 Skill | 实际只读取 Memory Book 标题，未读取 Markdown | 改为读取并原地更新完整 Agent Skill |
| `session_event_log` 是轨迹事实源 | ADR-056/057 已确立 `conversation_events` 为规范事实链 | 只读取规范 Conversation Event Store |
| 定时器可直接调用 Orchestrator | 重启/多实例会重复执行写操作 | 定时器只入持久队列，时间桶幂等 |

## 验收标准

- 主宿主启动后日志包含 `SubconsciousWorker` Started，且 `durableQueue=true`。
- 周期循环生成三种持久 JobType；同一时间桶重复入队保持幂等。
- 不同 workspace 或 agent 的成功轨迹不会交叉进入候选集。
- 失败、非零退出码或未配对的工具轨迹不会生成 Skill。
- 创建后真实存在 `SKILL.md`、`manifest.json`、`index.json`，并能被 `SkillEnforcer` 命中。
- 已处理来源 Turn 不会在每个 12 小时周期重复消耗候选检测 Token。
- 跨名称候选必须通过语义准入；低置信度自动延后，不创建 Skill。
- 自动去重只禁用重复项且记录规范 Skill，不能删除；同工具但不同意图的 Skill 必须保留。
- 改进基于完整 Markdown，版本号按 patch 递增且索引同步更新。

## 运行验收记录

2026-07-30 使用 `dev-up.py --restart` 部署后：

- 启动日志出现 `SubconsciousWorker Started` 且 `durableQueue=true`；`/health` 返回 200。
- `memory.auto_dream` 在首次 5 分钟窗口入队并完成，持久记录状态为 `completed`。
- `skill.extract_patterns` 在首次 10 分钟窗口入队并完成；当前规范事件库没有满足门禁的两步成功工具轨迹，因此报告 0 个候选，未生成低证据 Skill。
- 聚焦测试 7/7 通过，覆盖三种周期 JobType、同时间桶已完成作业不重开、workspace/agent 隔离、缺失 `exitCode` 的轨迹拒绝，以及创建/更新 Skill 后可被 `SkillEnforcer` 命中。

2026-08-01 完成 P0 真实数据闭环验收：

- 新增 `ILLMConfigResolver.ResolveRoleAsync`，统一解析 `conscious`、`subconscious` 与 smart 子代理角色；角色缺失、跨 workspace、profile 不一致或 registry 路由不存在时 fail-closed。
- Pre-Compaction Flush、上下文压缩摘要、潜意识 Worker、增强召回、每日摘要与语义触发工具均改为请求 `subconscious` 角色，不再在这些调用点绑定 Flash 或模板默认值。
- 记忆库写入语义去重使用 `ExperiencePackage.AgentInstanceId` 解析 `subconscious` 角色；旧 `SmartSearchAsync` 因没有 workspace/Agent 身份，在主宿主不再隐式启动 `subconscious/default-subconscious` 深度探索，避免 Context 构造阶段产生无归属的失败调用。主会话继续由 role-scoped recall pipeline 做 Flash 判断与排序。
- 主宿主恢复 `SessionCompressedMemoryMaintenanceHook`，并补齐 `MemoryWikiPageUpdateService` / `WikiPageWriteEntry` 注册。真实 Hook Job `28f2430056dc4590ab7308032db2af3e` 完成 `memory_wiki_page_update.v1`，写入 1 个 Wiki page，0 个校验错误。
- 历史成功轨迹提取发现 5 个候选、晋升 4 个 Agent 私有 Skill；随后改进任务评估 4 个并修补 1 个。轨迹读取改为在最多 200 条成功 Command 的有界窗口中先做质量过滤，避免近期简单任务饿死较早黄金路径。
- 后续周期提取又晋升 6 个 Skill，当前共有 10 个 `auto-generated` Skill；这证明周期链路已持续运行，也同时暴露了跨名称语义去重不足的问题。
- 从真实消息日志生成 16 份每日索引（2026-07-14、07-15、07-18 至 07-31）。新版归因下 15 次调用全部为 `deepseek/deepseek-v4-flash`，合计 265,337 tokens、约 ¥0.289192；相同日期和源哈希重跑会跳过且不再次调用 LLM。
- 每日摘要用量归到实际 `workspace=default`、`session=daily-summary:{day}` 与 `SourceId=llm:daily-summary:{invocationId}`，不再落入 `memory/subconscious-memory` 伪作用域。
- 验收期间在 LLM 调用中途重启，暴露出过期 `processing` 租约仍被并发统计占用的问题；队列统计现只把有效租约计为 Processing，并把过期租约暴露为 pending backlog。原 Job `5805091120ab4caaa2d6edd20fc4c038` 在修复部署后被新 Worker 重新租用并完成，评估 5 个 Skill、修补 1 个，0 retry、0 error。
- 聚焦回归通过：PuddingRuntime 12/12、Skill evolution 3/3、Memory Tools 19/19、PuddingPlatform 12/12、PuddingWebApi 10/10、PuddingMemoryEngine 14/14；主宿主构建成功，重启后四个服务在线且 `/health` 为 healthy。真实心跳完成 Context 构造后，新启动日志中无伪 `subconscious/default-subconscious` 路由且 Error 为 0。

2026-08-01 完成 P1 自值守准入与去重验收：

- 真实 `skill.improve` Job `d9c2780b22814e36b5d38c54eff7aae5` 由 `deepseek-v4-flash` 自主聚类，确定性门禁批准 3 个重复项：健康检查、普通图像发送、参考图像发送各 1 个；重复 Skill 均保留文件并设为禁用，0 error、0 retry。
- 同一任务评估 5 个启用 Skill，自动修补 3 个、跳过 2 个；报告 `operationCount=6`，完整保存 `consolidated_count`、`disabled_duplicate_skill_ids` 等审计字段。
- 该真实任务共 9 次 Flash 调用，8,204 input + 31,922 output = 40,126 tokens，约 ¥0.071546。由此补充版本水位，避免未变化版本每 4 小时重复执行同一批评估。
- 真实 `skill.extract_patterns` Job `1d09a42fcfaf4d93a9b3cdcb3b3940e6` 在调用 LLM 前抑制 5 条已处理 Turn，返回 0 candidates、0 operations、0 errors，日志中没有 `pattern-detect` / `skill-admission` 调用。
- 合并时 manifest 与 SKILL.md frontmatter 版本现同步递增；聚焦测试 6/6（语义准入/安全合并/来源去重/选择性版本水位）和 11/11（Worker 报告/运行时 Skill 存储）通过，主宿主 0 error 构建通过。

## 已知限制与后续

- 当前周期配置面向一个默认 Agent；多 Agent 自动枚举与各自调度频率另行设计。
- 新 Skill 通过成功轨迹、可复用性、安全和语义准入门禁后自动启用；系统以高置信度阈值和 fail-closed 自动延后来代替人工审批。
- 当前质量证明是“工具轨迹成功”，不是隔离环境中的 Skill 重放测试；若需要更强保证，应新增 sandbox replay，而不是把 LLM 判断当作执行证明。
- P1 已增加晋升前语义准入和周期性跨名称语义聚类；确定性复核要求工具指纹一致并达到文本/来源证据门槛，重复项只禁用不删除。更强质量证明仍依赖后续 sandbox replay。
- 历史每日摘要已回填；历史 compaction 事件不宜无质量门槛地全量重放进 Wiki。后续应增加带游标、来源去重和批次审阅的专用 backfill，而不是手工批量发布 Hook。

## 参考

- [Hermes Agent Skills 文档](https://github.com/NousResearch/hermes-agent/blob/main/website/docs/user-guide/features/skills.md)
- [Hermes Agent Skill 工作指南](https://github.com/NousResearch/hermes-agent/blob/main/website/docs/guides/work-with-skills.md)
- [hermes-agent-self-evolution（独立实验）](https://github.com/NousResearch/hermes-agent-self-evolution)
- ADR-048: Hermes 型系统开发方向参考
- ADR-056 / ADR-057: 可靠命令与规范 Conversation 事件流
