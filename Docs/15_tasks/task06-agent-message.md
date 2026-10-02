# Task 06 - Agent 消息系统（与中心锁协同）
状态：`design`  
优先级：`P1`  
最后更新：2026-02-20

## 1. 目标
- 为多 Agent 提供可控的异步通信机制（收件箱、优先级、可中断通知）。
- 与中心锁（Task 23）联动，在冲突场景下提供“请求释放/等待/强制处理”闭环。
- 在单仓与 worktree 两种模式下保持一致行为。

## 2. 依赖关系
- 上游依赖：`task23-central-lock-coordination.md`
- 关系：Task 23 负责“资源互斥”，Task 06 负责“消息协同”，两者通过同一协调中枢（Coordination Hub）统一编排。

## 3. 消息语义模型
### 3.1 消息优先级
- `normal`：普通消息。任务阶段结束后注入。
- `important`：重要消息。下一次 function call 前提醒并要求检查 Inbox。
- `urgent`：紧急消息。触发中断请求（遵守编辑原子性）。

### 3.2 消息结构（v1）
- `id`
- `from` / `to`
- `type`
- `content`
- `priority`
- `timestamp`
- `metadata`（可选：taskId、lockId、path、worktree）

## 4. 投递与消费时机
### 4.1 被动注入（Context Injection）
- Agent 进入下一轮推理前，系统检查 Inbox 并注入摘要。

### 4.2 主动拉取（Tool Polling）
- 提供 `check_messages()` / `read_inbox()` 函数。
- Leader 可要求 Worker 在关键步骤前强制检查。

### 4.3 中断重规划（Interrupt & Re-plan）
- `urgent` 可触发中断请求。
- 若目标 Agent 处于编辑事务内（原子区），先标记“待中断”，事务结束后再中断。

## 5. 与中心锁协同（关键）
### 5.1 锁冲突时的消息闭环
1. Agent B 访问命中 A 的锁，返回冲突错误。
2. 系统自动生成 `unlock_requested` 消息发送给 A（和 Leader 可见）。
3. A 可主动释放；Leader 可强制解锁。

### 5.2 事件总线
最小事件集：
- `message_posted`
- `inbox_checked`
- `inbox_drained`
- `agent_interrupt_requested`
- `agent_interrupted`
- `unlock_requested`

## 6. 协调中枢（SwarmCoordinationHub）
建议中枢内部包含：
- `MessageHub`（收件箱、优先级、投递）
- `LockManager`（来自 Task 23）
- `AgentRuntimeRegistry`（记录 Agent 当前阶段：Idle/Thinking/Editing/Streaming）

## 7. API / Function 设计（v1）
- `post_message(to, content, priority, type, metadata)`
- `check_messages(limit, min_priority)`
- `read_inbox(clear=true)`
- `request_unlock(lock_id, reason)`
- `interrupt_agent(agent_id, reason)`（Leader / urgent）

## 8. 错误与指引
冲突返回应包含：
1. 锁 owner
2. 锁目标
3. 建议动作（等待、request_unlock、联系 Leader）

## 9. 实施分期
1. `v1-a`：消息结构升级（priority + metadata）+ Inbox 读写
2. `v1-b`：`check_messages` 工具与优先级注入
3. `v1-c`：与中心锁联动（unlock 请求消息）
4. `v1-d`：紧急中断（带编辑原子性）

## 10. DoD
1. Agent 间可发送 normal/important/urgent 三类消息。
2. Inbox 可查询、可清空、可按优先级排序。
3. 锁冲突自动触发 unlock 请求消息。
4. urgent 中断遵守编辑原子性，不破坏写事务。
5. CLI 可见消息摘要与未读计数。
