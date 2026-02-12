# Task Dynamic Planning Design

> 日期：2026-06-08
> 状态：draft
> 范围：多 Agent / 子代理任务规划、委派、深度限制、团队管理和可观测性设计

## 1. 背景

PuddingAgent 已具备 Agent 模板、Workspace Agent、子代理、Agent 间消息投递和运行归档等基础能力。下一步需要把这些能力组织成一个任务动态规划系统：Leader Agent 接收任务后，能基于当前团队和模板能力进行研究、拆解、委派、追踪、回收和重规划。

这里的“动态规划”不是传统算法意义上的 Dynamic Programming，而是“动态任务规划”：任务树不是一次性静态生成，而是在研究结果、子任务反馈、Agent 可用性和失败状态变化后持续修订。

## 2. 当前基线

当前代码已经提供以下基础：

- 全局 Agent 模板包含 `Service`、`Task`、`Audit` 三类，内置模板包括 `workspace-service-agent`、`workspace-task-agent`、`code-agent`、`workspace-audit-agent`。
- 文件式全局模板管理支持 `Role`、能力、LLM profile、SOUL/TOOLS/AGENTS/MEMORY 等模板材料。
- Workspace Agent API 已支持按模板创建、更新、删除、冻结和解冻 Agent 的接口骨架。
- `spawn_sub_agent` 支持基于模板派生子代理、同步/异步执行、工具子集、能力下调。
- `ISubAgentManager` 和 run archive 已能记录子代理运行、父会话状态、内部事件和终态。
- Agent-to-Agent Message Fabric 已支持可见消息、durable delivery、idle gating、retry/dead-letter 和结构化日志。
- `WorkspaceAgentsContextBuilder` 已能把可通信 Agent 列表注入运行上下文。

主要缺口：

- 没有 durable task plan / task node 账本。
- 子代理和 Agent 委派没有显式 `depth`、`maxDepth`、`parentTaskNodeId`。
- Leader 可以通过自然语言“计划”，但缺少后端可审计的计划状态流转。
- Workspace Agent 管理能力尚未暴露为受控 Leader 工具。
- 子代理上下文没有系统级注入“我在任务树中的位置和限制”。
- 动态重规划、任务回收、任务 supersede 还没有状态模型。

## 3. 目标

V1 目标：

- Leader Agent 能创建一棵 durable task plan。
- Leader 能把任务节点分配给自己、Workspace Agent 或 SubAgent。
- Research Agent 能承接研究节点并返回结构化研究结果。
- Planner 能把研究结果拆解成可执行任务节点。
- 被委派 Agent 可以在策略允许时继续拆分任务，但受到最大深度限制。
- 默认最大委派深度为 2。
- 子代理上下文中必须包含其任务位置、深度、约束和输出协议。
- 所有计划、委派、执行、完成、失败、回收和重规划都有可查询记录。

非目标：

- V1 不实现复杂自动资源调度。
- V1 不实现跨机器 leader election。
- V1 不实现强化学习式规划优化。
- V1 不允许无限递归创建 Agent 或子代理。
- V1 不把任务规划逻辑塞进 `AgentExecutionService`。

## 4. 推荐架构

新增一个独立的 Durable Task Plan Engine，作为 Agent 执行层上方的协作规划层。

```text
User Task
  -> Leader Agent
  -> TaskPlanService
  -> Research Node
  -> Planner Node
  -> TaskNode tree
  -> TaskAssignmentService
     -> Workspace Agent through Message Fabric
     -> SubAgent through ISubAgentInvocationService
     -> Leader self execution
  -> TaskPlanStore
  -> Observability / Diagnostics
```

核心原则：

- `TaskPlanService` 负责计划状态，不负责直接执行 LLM。
- `TaskAssignmentService` 负责委派路由，不持有复杂执行循环。
- `ISubAgentInvocationService` 继续负责子代理执行生命周期。
- `IMessageSystem` / Message Fabric 继续负责 Workspace Agent 间可见消息和 durable delivery。
- `AgentExecutionService` 只消费上下文和执行请求，不承载任务树算法。

## 5. 核心模型

### TaskPlanRun

一次用户目标对应一个 `TaskPlanRun`。

建议字段：

```text
plan_id
workspace_id
root_session_id
leader_agent_id
objective
status: draft | active | completed | failed | cancelled
max_depth
created_at
updated_at
completed_at
trace_id
correlation_id
```

### TaskNode

任务树中的每个节点都是一个 `TaskNode`。

建议字段：

```text
task_node_id
plan_id
parent_task_node_id
depth
title
objective
input_context_summary
expected_output_contract
assigned_to_kind: leader | workspace_agent | sub_agent | unassigned
assigned_to_id
assigned_template_id
status: draft | planned | assigned | running | blocked | completed | failed | cancelled | superseded
allow_sub_delegation
allow_agent_creation
created_by_agent_id
result_summary
result_artifact_ref
error_message
created_at
updated_at
started_at
completed_at
trace_id
correlation_id
```

### TaskDelegationPolicy

后端策略对象负责判断是否允许委派：

```text
current_depth < max_depth
agent_role allows delegation
template allows delegation
tool permission allows requested assignment
active node count below plan limit
workspace allows leader-managed team changes
```

这必须是后端强制，不只依赖 prompt。

## 6. 深度限制策略

默认配置：

```json
{
  "taskPlanning": {
    "maxDelegationDepth": 2,
    "defaultAllowSubDelegation": true,
    "allowAgentCreationByLeader": true,
    "maxActiveTaskNodesPerPlan": 50
  }
}
```

深度语义：

- `depth = 0`：Leader 接收原始用户任务。
- `depth = 1`：Leader 直接委派给 Research Agent、Planner、Task Agent 或 SubAgent。
- `depth = 2`：一级 Agent 再拆出的子任务。
- `depth >= maxDepth`：只能执行或汇报，不能继续拆分、创建 Agent 或派生子代理。

需要改造的请求边界：

- `SubAgentInvocationRequest` 增加 `PlanId`、`ParentTaskNodeId`、`Depth`、`MaxDepth`。
- `SubAgentSpawnRequest` 增加同样字段。
- `spawn_sub_agent` 在调用前检查 depth policy。
- `assign_task` 和 `split_task` 工具在写入 task node 前检查 policy。
- `TaskPlanStore` 持久化 depth，避免进程重启后绕过限制。

## 7. 子代理上下文注入

每个被委派的 Agent 或 SubAgent 都应收到系统生成的任务规划上下文。

```text
--- TASK PLANNING CONTEXT ---
plan_id: {plan_id}
task_node_id: {task_node_id}
parent_task_node_id: {parent_task_node_id}
delegation_depth: {depth}
max_delegation_depth: {max_depth}
role_in_plan: researcher | planner | executor | reviewer
allowed_to_delegate: true | false
allowed_to_create_agents: true | false
assigned_objective: {objective}
expected_output:
- findings
- evidence
- risks
- next_tasks_if_any
constraints:
- do not exceed max delegation depth
- do not create agents unless allowed
- report completion through task result tool
```

该上下文应由 `TaskPlannerContextBuilder` 或现有 context assembly 层注入，不能由 Leader 自由拼接。这样可以保证每个子代理都知道自己的位置、深度和边界。

## 8. Leader 团队管理

Leader 可以基于模板管理团队，但必须通过受控工具完成。

建议新增工具：

- `list_team_agents`
- `create_team_agent`
- `update_team_agent`
- `retire_team_agent`
- `assign_task`
- `split_task`
- `report_task_result`

底层复用：

- `WorkspaceAgentFileService` 创建和更新 Workspace Agent。
- `WorkspaceAgentApiController` 的语义作为 HTTP/API 对齐层。
- `AgentTemplateFileService` 校验模板是否存在、启用、角色匹配。

权限规则：

- 只有 Leader 或显式授权的 Task Agent 能创建或回收 Agent。
- Audit Agent 不允许创建、回收或提升 Agent 权限。
- 创建 Agent 必须基于已启用模板。
- 新建 Agent 的权限不得超过 Leader 当前计划授权边界。
- 回收 Agent 默认 disable/archive，不硬删历史运行数据。

## 9. Research 和 Planner 角色

建议补充两个全局模板：

### Research Agent

职责：

- 搜集事实、代码、文档、历史上下文。
- 输出证据、风险、缺口。
- 不直接执行高风险写操作。

输出协议：

```text
findings
evidence
unknowns
risks
recommended_next_tasks
```

### Planner Agent

职责：

- 把目标和研究结果拆成任务树。
- 标注依赖、优先级、输出契约和建议执行者。
- 不直接执行代码修改。

输出协议：

```text
task_nodes
dependencies
assignment_recommendations
acceptance_criteria
risk_controls
```

## 10. 状态流转

推荐最小状态机：

```text
draft -> planned -> assigned -> running -> completed
                               -> blocked
                               -> failed
                               -> cancelled
planned/running/blocked/failed -> superseded
```

规则：

- `completed`、`cancelled`、`superseded` 是 terminal 状态。
- `failed` 可以被 Leader 重规划并 supersede。
- `blocked` 必须有阻塞原因和需要的输入。
- `superseded` 必须指向替代节点或重规划批次。

## 11. 可观测性

结构化日志建议：

```text
[TaskPlanning] plan_created
[TaskPlanning] node_created
[TaskPlanning] node_assigned
[TaskPlanning] delegation_denied
[TaskPlanning] node_status_changed
[TaskPlanning] node_result_reported
[TaskPlanning] plan_completed
```

关键字段：

```text
workspace_id
plan_id
task_node_id
parent_task_node_id
depth
max_depth
assigned_to_kind
assigned_to_id
status
trace_id
correlation_id
causation_id
```

V1 可以先用 `ILogger`。如果后续已有 RuntimeActivitySink 接入，再把重要事件映射为 runtime activity。

## 12. 与现有系统的关系

推荐映射：

| 规划动作 | 现有系统落点 |
| --- | --- |
| Agent 间任务委派 | `IMessageSystem` / Message Fabric |
| 短生命周期子任务 | `ISubAgentInvocationService` |
| 子代理运行归档 | `ISubAgentRunStore` |
| Workspace Agent 创建/回收 | `WorkspaceAgentFileService` |
| 团队上下文 | `WorkspaceAgentsContextBuilder` + 新 `TaskPlannerContextBuilder` |
| 审计 | Audit Agent + 结构化 TaskPlan 只读视图 |

## 13. 分阶段落地

### Phase 1：Durable Task Plan Store

- 新增 `TaskPlanRun` 和 `TaskNode` 模型。
- 新增 store、状态流转和查询 API。
- 新增 `split_task`、`assign_task`、`report_task_result` 的最小工具。

### Phase 2：深度限制和上下文注入

- 配置 `taskPlanning.maxDelegationDepth`，默认 2。
- 给 SubAgent invocation/spawn 请求增加 plan/depth 字段。
- 在 `spawn_sub_agent`、`assign_task`、`split_task` 强制 depth policy。
- 注入 `TASK PLANNING CONTEXT`。

### Phase 3：Leader 团队管理

- 新增 Leader 工具：创建、更新、回收 Workspace Agent。
- 复用模板文件服务和 Workspace Agent 文件服务。
- 增加角色和权限校验。

### Phase 4：Research / Planner 模板

- 增加 Research Agent 和 Planner Agent 全局模板。
- 为模板定义输出协议和工具白名单。
- 在 Leader 的规划流程中优先调用 Research，再调用 Planner。

### Phase 5：动态重规划

- 支持 blocked/failed 节点重规划。
- 支持 `superseded` 状态和替代节点关系。
- 汇总子任务结果给 Leader 做最终输出。

## 14. 风险和约束

主要风险：

- 只靠 prompt 限制深度会被多轮委派绕过。
- Leader 无限创建 Agent 会造成资源泄漏和团队膨胀。
- 没有 durable task node 时，失败恢复和审计不可控。
- 任务结果只存在聊天上下文时，动态重规划缺少可靠输入。
- 如果把规划逻辑放入 `AgentExecutionService`，会加重已有执行引擎职责过载。

控制策略：

- 深度、权限和状态必须后端强制。
- Agent 创建必须通过受控工具。
- 任务计划必须持久化。
- 每个子代理上下文必须由系统注入约束。
- V1 先实现最小可运行闭环，再做自动优化。

## 15. 设计结论

任务动态规划应作为 PuddingAgent 的上层协作规划能力实现，而不是重写现有子代理或消息系统。

推荐路径是新增 Durable Task Plan Engine，复用现有 Agent 模板、Workspace Agent 管理、Message Fabric、SubAgentInvocationService 和 run archive。默认最大深度设为 2，既支持 Leader 到一级 Agent，再到二级子任务的协作深度，也能避免递归套娃。

下一步应先写实施计划，优先实现 durable task plan store、depth policy 和任务上下文注入，再扩展 Leader 团队管理和动态重规划。
