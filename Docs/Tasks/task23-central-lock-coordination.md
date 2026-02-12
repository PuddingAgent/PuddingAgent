# Task 23 - 中心锁与协同通知总线（Swarm 冲突治理）
状态：`design`  
优先级：`P1`  
最后更新：2026-02-20

## 1. 目标
- 通过“中心锁 + 消息通知”降低多 Agent 并发修改冲突。
- 让锁的申请/释放默认隐式自动化，减少 LLM 负担。
- 在单仓与 Git worktree 两种模式下提供统一冲突行为与可恢复路径。

## 2. 核心原则
- 锁是程序内建逻辑锁，不依赖 OS 文件锁。
- 编辑前自动加锁，编辑完成/提交后自动释放。
- LLM 可主动释放锁；Leader 拥有最高权限（查看/强制解锁/强制加锁）。
- 冲突时必须返回明确错误与下一步指引（等待、请求释放、联系 Leader）。

依赖：
- `Docs/Tasks/task06-agent-message.md`（unlock 请求、优先级通知、紧急中断语义）

## 3. 锁模型（v1）
| 字段 | 说明 |
|---|---|
| `lockId` | 锁唯一标识 |
| `scope` | `file` / `directory` |
| `targets` | 被锁定路径列表（支持目录） |
| `type` | `edit` / `commit` / `time_window` |
| `owner` | `agentId` / `agentName` / `role` |
| `createdAt`/`expireAt` | 创建时间/过期时间 |
| `status` | `active` / `released` / `expired` / `force_released` |
| `description` | 锁描述（任务上下文） |
| `meta` | 会话 ID、任务 ID、worktree 等扩展字段 |

权限规则：
- 默认仅 owner 可解锁。
- Leader 可解任何锁、可强制加锁（文件/目录、全局或白名单）。

## 4. 两种工作模式行为
### 4.1 单仓模式（主力）
- Agent 尝试读/写命中冲突锁：直接报错并返回指引。
- Leader 编排时尽量做目录/模块边界隔离，减少锁冲突。

### 4.2 Git worktree 模式
- 仍执行中心锁检查（避免跨 worktree 的逻辑冲突）。
- 冲突行为与单仓一致；优先通过任务边界隔离避免冲突。

## 5. 通知总线（Coordination Bus）
事件最小集：
- `lock_acquired`
- `lock_denied`
- `lock_released`
- `lock_force_released`
- `lock_expired`
- `unlock_requested`

用途：
- CLI 右侧状态区显示锁摘要（总数、冲突、等待）。
- Agent 间消息协调（请求 owner 释放、Leader 决策）。
- 后续可接入审计与统计。

## 6. API / Function 设计（v1）
- `acquire_lock(targets, type, ttl, description)`  
- `release_lock(lock_id)`  
- `list_locks(filter)`  
- `request_unlock(lock_id, reason)`  
- `force_release_lock(lock_id)`（Leader only）  
- `force_acquire_lock(targets, type, ttl, description)`（Leader only）

默认策略：
- 工具写入前自动尝试 `acquire_lock`。
- 写入完成后自动 `release_lock`。
- 提交锁在提交完成后释放；时间锁按 TTL + 心跳续租。

## 7. 持久化与恢复
- 全局锁表：`.pudding/locks/lock-state.json`
- 通知日志：`.pudding/locks/events.log`（可选）
- 启动恢复：
1. 读取锁表
2. 清理过期锁
3. 广播 `lock_expired`

## 8. 错误与指引文案规范
- 冲突错误示例：  
  `File is locked by agent worker-12 (Implementer). Use request_unlock or wait for release.`
- 指引必须包含：
1. 锁 owner
2. 锁类型与目标
3. 可用动作（等待/请求释放/Leader 强制处理）

## 9. 实施顺序（建议）
1. `v1-a`：`LockManager` + 锁表持久化 + TTL
2. `v1-b`：文件编辑链路自动加锁/自动释放
3. `v1-c`：`/locks` 命令与右侧状态面板
4. `v1-d`：Leader 强制解锁/强制加锁
5. `v1-e`：通知总线事件与审计

## 10. DoD
1. 多 Agent 同时编辑同一文件时，后到者收到冲突错误与指引。
2. 编辑完成后锁可自动释放，锁泄漏可通过 TTL 恢复。
3. Leader 可查看全部锁并执行强制操作。
4. `/locks` 与状态面板可见当前锁、冲突、等待。
5. 单仓与 worktree 两种模式行为一致可预测。
