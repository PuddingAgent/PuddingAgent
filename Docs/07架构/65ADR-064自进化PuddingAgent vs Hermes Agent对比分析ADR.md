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
  ├─ LLM 判断任务是否可复用
  ├─ passing_check / reusable / safe 三项必须全部通过
  └─ 同名确定性 skillId 已存在时跳过，避免周期重复创建
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

`ImproveSkillsAsync(workspaceId, agentInstanceId)` 只枚举该 Agent 的 `auto-generated` Skills，读取完整 `SKILL.md` 交给 LLM 评估。只有明确存在步骤矛盾、缺少验证或过时内容时才写回；写回使用同一个 `AgentSkillFileService.UpdateAsync`，同时更新 Markdown、manifest、索引和 patch 版本号。

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
| `extract_patterns` | `skill.pattern_extraction.v1` | candidates/promoted/demoted/skipped count、created Skill IDs |
| `improve_skills` | `skill.improvement.v1` | evaluated/patched/skipped count、improved Skill IDs |

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
- 改进基于完整 Markdown，版本号按 patch 递增且索引同步更新。

## 运行验收记录

2026-07-30 使用 `dev-up.py --restart` 部署后：

- 启动日志出现 `SubconsciousWorker Started` 且 `durableQueue=true`；`/health` 返回 200。
- `memory.auto_dream` 在首次 5 分钟窗口入队并完成，持久记录状态为 `completed`。
- `skill.extract_patterns` 在首次 10 分钟窗口入队并完成；当前规范事件库没有满足门禁的两步成功工具轨迹，因此报告 0 个候选，未生成低证据 Skill。
- 聚焦测试 7/7 通过，覆盖三种周期 JobType、同时间桶已完成作业不重开、workspace/agent 隔离、缺失 `exitCode` 的轨迹拒绝，以及创建/更新 Skill 后可被 `SkillEnforcer` 命中。

## 已知限制与后续

- 当前周期配置面向一个默认 Agent；多 Agent 自动枚举与各自调度频率另行设计。
- 生成 Skill 会立即启用，因此必须保留成功轨迹、可复用性与安全三重门禁。后续可增加人工审批模式。
- 当前质量证明是“工具轨迹成功”，不是隔离环境中的 Skill 重放测试；若需要更强保证，应新增 sandbox replay，而不是把 LLM 判断当作执行证明。

## 参考

- [Hermes Agent Skills 文档](https://github.com/NousResearch/hermes-agent/blob/main/website/docs/user-guide/features/skills.md)
- [Hermes Agent Skill 工作指南](https://github.com/NousResearch/hermes-agent/blob/main/website/docs/guides/work-with-skills.md)
- [hermes-agent-self-evolution（独立实验）](https://github.com/NousResearch/hermes-agent-self-evolution)
- ADR-048: Hermes 型系统开发方向参考
- ADR-056 / ADR-057: 可靠命令与规范 Conversation 事件流
