# 长效学习与记忆机制 — 设计文档

> 目标：建立一套由框架（Pudding）强制执行的长效学习机制，不依赖 Agent 显意识的提示词驱动，由潜意识 LLM 后台异步完成。

> 2026-08-14 架构更新：本文件保留学习目标与管道内容；触发、生命周期和可靠性以 `Docs/deepseek-harness-pi-plugin-hook-event-architecture-2026-08-14.md` 为准。新实现不再把所有框架通知统称为 Hook，而是区分同步 Typed Hook、提交后的 durable lifecycle event、持久 Job 与定时兜底 Command。

## 一、核心原则

### 铁律
> **记忆系统的维护（整理、提炼、去重、索引）必须由 Pudding 框架通过潜意识 LLM + 硬编码管道自动完成，不能依赖 Agent 显意识的提示词驱动。**

### 为什么不能依赖"记忆"（Agent 显意识）
- Agent 会话结束后，上下文丢失，细节遗忘
- 每次新会话需要重建上下文（浪费 tokens）
- 显意识驱动的维护不可靠（Agent 可能忘记、跳过、或执行不完整）
- 缓存命中率直接受显意识行为影响（写记忆→PINNED变化→缓存失效）

### 参考模式
- **Claude**: 会话结束后的后台处理，提取长期记忆
- **Hermes Agent**: 结构化的经验提取 → 技能固化流程

---

## 二、三层架构

```
┌─────────────────────────────────────────────┐
│  意识层 (Pro Model)                          │
│  - 用户对话                                  │
│  - 调用工具、委派子代理                      │
│  - 读/写记忆（save_memory, search_memory）   │
│  - 不负责记忆维护                            │
│  - 不负责经验提取                            │
└──────────────┬──────────────────────────────┘
               │ Typed Hook / durable event
               ▼
┌─────────────────────────────────────────────┐
│  潜意识层 (Flash Model) — 异步后台           │
│                                              │
│  ┌─────────────────────────────────────┐     │
│  │ 管道 1: 记忆维护                     │     │
│  │  - 去重合并                          │     │
│  │  - 过期清理                          │     │
│  │  - 索引更新                          │     │
│  │  - 档案建设（用户/项目）             │     │
│  └─────────────────────────────────────┘     │
│  ┌─────────────────────────────────────┐     │
│  │ 管道 2: 经验提取 → SKILL            │     │
│  │  - 从会话中识别可复用模式            │     │
│  │  - 提取为 SKILL 候选                 │     │
│  │  - 更新已有 SKILL                    │     │
│  │  - 质量评分                          │     │
│  └─────────────────────────────────────┘     │
└──────────────┬──────────────────────────────┘
               │ 写入
               ▼
┌─────────────────────────────────────────────┐
│  存储层                                      │
│  - 记忆图书馆 (Books/Chapters)               │
│  - SKILL 文件系统                            │
│  - 外部目录/文件（项目档案）                 │
│  - 会话日志                                  │
└─────────────────────────────────────────────┘
```

---

## 三、事件驱动触发点

潜意识 LLM 不在 Hook 或 EventDispatcher 中直接运行。同步 Hook 只处理必须发生在当前提交前的短操作；提交后的 durable event 经独立 consumer checkpoint 转换为持久 Job，再由后台 Worker 执行 LLM 工作。

| 触发合同 | 类型 | 触发时机 | 执行管道 | 优先级 |
|------|------|---------|---------|--------|
| `context.compaction.before_commit` | Typed Hook | 压缩提交前 | Pre-Compaction Flush（同步、有界） | P0 |
| `agent.run.settled` | Durable event | Run 已无 retry/compaction/follow-up/子工作 | 管道1+2 增量信号 | P0 |
| `session.closed` | Durable event | Session 真正关闭 | 管道1+2 会话收尾 | P0 |
| `context.compaction.completed` | Durable event | 压缩成功提交 | 管道1增量更新 | P0 |
| `memory.written` | Durable event | 记忆写入成功提交 | 管道1去重/索引信号 | P1 |
| `learning.proposal.created` | Durable event | 形成 Skill/Prompt/策略修订提案 | 评测与审批，不直接激活 | P1 |
| `ScheduleMaintenance` | Command | 周期或阈值兜底 | Auto-Dream/深度整理 Job | P2 |
| `heartbeat.completed` | Durable event | 自主推进轮次完成 | 低权重健康/轨迹信号 | P2 |

### 关键约束

- 框架必需 Hook 和事件提交点由**框架强制执行**，Agent 无法通过提示词跳过
- 潜意识 LLM 使用 **Flash 模型**（低成本、低延迟）
- Event handler 只校验并入 Job，不在事件派发线程执行长 LLM
- 每个 consumer group 独立 checkpoint、retry 和 dead-letter；业务写入使用来源 event id + pipeline version 幂等
- Timer 只产生幂等 Command，不能直接扫描并执行学习逻辑
- 处理结果写入后发布对应 durable fact；Prompt 投影监听这些事实并按频率合并刷新

---

## 四、管道 1：记忆维护

### 输入
- 当前记忆图书馆全量快照
- 最近 N 个会话的消息日志
- 当前用户档案、项目档案

### 处理步骤

```
1. 去重分析
   ├─ 识别相同 title + 相同 summary 的重复 Book
   ├─ 合并内容 → 保留最新/最完整的
   └─ 删除空壳

2. 过期清理
   ├─ 四步判断法（准确? 有用? 一句话? 哪层?）
   ├─ 标记 [已过时] 或删除
   └─ 归档超过 30 天未引用的内容

3. 档案建设
   ├─ 用户档案: 从对话中提取稳定个人事实
   │   └─ 姓名、角色、技能、偏好、沟通风格
   ├─ 项目档案: 从对话中提取项目元信息
   │   └─ 技术栈、架构、关键决策、当前状态
   └─ 写入外部文件（可选）:
       └─ {project}/memory/project-profile.md

4. 索引维护
   └─ 更新 memory/INDEX.md
```

### 输出格式
```
save_memory → 只写指针（一句话摘要 + 文件路径）
file_write  → 大段内容写入 memory/ 目录
```

---

## 五、管道 2：经验提取 → SKILL

### 输入
- 最近 N 个会话的完整消息日志
- 当前已注册的 SKILL 列表
- Agent 的性能指标（agent_diagnostics）

### 处理步骤

```
1. 模式识别
   ├─ 扫描对话，识别:
   │   ├─ 重复被问到的问题 → 可固化为 SKILL
   │   ├─ 成功的多步骤流程 → 可固化为 SKILL
   │   ├─ 调试/诊断方法论 → 可更新已有 SKILL
   │   ├─ 工具使用的陷阱 → 可更新已有 SKILL
   │   └─ 新的最佳实践 → 可固化为 SKILL
   └─ 对每个候选打分:
       ├─ 复用频率 (出现过几次?)
       ├─ 通用性 (跨项目适用?)
       └─ 收益 (能节省多少 tokens/时间?)

2. 候选生成
   ├─ 为高分候选起草 SKILL.md
   ├─ 包含: 名称、版本、描述、标签、步骤
   └─ 创建 immutable revision proposal（不覆盖 active Skill）

3. 已有 SKILL 更新
   ├─ 对比现有 SKILL 与新发现的模式
   ├─ 提出 v1.x → v1.y 的增量更新
   └─ 标记更新原因和证据

4. 质量门禁
   ├─ 新 SKILL 必须通过 code-qa-verification
   ├─ 更新 SKILL 必须有至少 2 个会话的证据支持
   ├─ test/evaluation/replay 数据默认不进入生产学习
   └─ 审批或 canary 后才能激活；持续监测并支持 rollback
```

### 输出格式
```
SKILL 草稿 → learning.proposal.created（immutable revision）
SKILL 评测 → learning.evaluation.completed
激活/回滚 → learning.revision.activated / learning.revision.rolled_back
通知用户 → "从最近 N 个会话中发现了 X 个可固化的经验"
```

---

## 六、档案建设协议

### 用户档案
```
位置: 记忆图书馆 "用户档案" Book
      外部: memory/profiles/user-profile.md

结构:
- 个人信息 (姓名、角色)
- 技能栈
- 偏好 (沟通风格、工具偏好、开发原则)
- 当前关注 (正在进行的项目/目标)
- 更新历史
```

### 项目档案
```
位置: 记忆图书馆 "项目知识" Book (已有)
      外部: {project_root}/memory/project-profile.md

结构:
- 项目概述
- 技术架构
- 当前状态
- 关键决策记录
- 已知问题
- 改进路线图
```

---

## 七、与显意识 Agent 的接口

Agent 显意识**不需要知道**潜意识 LLM 在做什么。但它可以通过以下方式受益：

| Agent 操作 | 潜意识 LLM 如何增强 |
|-----------|-------------------|
| `search_memory` | 返回经过去重、整理后的干净结果 |
| `agent_skill(list)` | 包含最新固化/更新的 SKILL |
| `goal_read` | 档案和索引保持最新 |
| 会话启动 | PINNED 层包含最新指针（由潜意识维护） |

---

## 八、实施优先级

| 优先级 | 事项 | 工作量 | 依赖 |
|--------|------|--------|------|
| **P0** | 生命周期词典 + Typed Hook + durable event/outbox/checkpoint | 大 | 无 |
| **P0** | 管道1: 记忆去重+过期清理 | 中 | durable event + Job |
| **P0** | 管道1: 档案建设（用户+项目） | 小 | durable event + Job |
| **P1** | 管道2: 经验识别+SKILL候选生成 | 大 | learning signal/candidate |
| **P1** | 管道2: 已有SKILL增量更新 | 中 | 管道2基础 |
| **P1** | Proposal 评测、审批、canary、rollback | 大 | immutable revision |
| **P2** | 外部文件档案同步 | 小 | 管道1 |
| **P2** | 历史事件离线 replay 对比新算法 | 中 | DomainEventLog |

---

## 九、与现有基础设施的关系

| 现有组件 | 复用方式 |
|---------|---------|
| `SubconsciousWorkerService` | 已存在，作为潜意识 LLM 的宿主 |
| `SubconsciousJobQueue` | 已存在，复用 lease/retry/dead-letter；后续由通用 Job Runtime 承载 |
| `SubconsciousRecallPipeline` | 已存在，处理异步记忆召回 |
| `IInternalEventBus` / `PriorityEventQueue` | 迁移基础；后续补 DomainEventLog、Outbox、per-consumer checkpoint |
| `PluginManifestCatalog` | 工具插件基线；后续把各学习阶段注册为 event/job plugins |
| `memory-system-v2-requirements.md` | 已定义 R1-R9，本设计细化之 |
| `memory-compaction` SKILL | 方法论指导（四步判断法等） |
| `skill-lifecycle` SKILL | 管理 SKILL 创建/更新/版本 |
| `self-evolution-methodology` | 闭环方法论 |

---

## 十、关键度量

| 指标 | 目标 | 测量方式 |
|------|------|---------|
| 记忆 Book 数 | < 25 | `grep_memory(list_books)` |
| 重复 Book 数 | 0 | `manage_memory(dedup_report)` |
| 缓存命中率 | > 85% | `agent_diagnostics(cache_health)` |
| SKILL 自动发现率 | 每月 1-3 个候选 | 日志统计 |
| 记忆维护耗时 | < 30s/次 | 内部指标 |

---

## 十一、事件溯源与学习治理

每个学习结果必须记录：来源 event ids、workspace/agent/session/run、origin、pipeline plugin id/version、输入窗口、评测版本、批准主体和当前 revision。事件重放可以重新生成候选与离线评测，但默认关闭文件写入、外部通知和 active revision 切换等生产副作用。

学习闭环固定为：

```text
signal observed
  -> eligibility/filter
  -> candidate
  -> immutable proposal
  -> offline evaluation
  -> review/canary
  -> activation
  -> outcome monitoring
  -> stable or rollback
```

同一来源操作的幂等和内容语义去重必须分开。来源 event id + pipeline version 只防止重复处理；是否合并两条记忆或两个 Skill 候选由独立策略与证据判断。
