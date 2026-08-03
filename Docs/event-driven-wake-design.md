# 事件驱动唤醒设计稿（任务 2）

状态：Proposed（待实施）
日期：2026-08-03
作者：蜜糖（链路测绘：sub-26723b93，全部结论带 file:line 证据）

## 1. 目标

- 消息到达即唤醒目标 Agent，消灭"等心跳"空转。
- 心跳从主驱动降级为兜底巡检。
- 硬约束：
  1. 不破坏去重首次语义（同一 message_id 仅首次持久化时发布事件）。
  2. 不引入同一 Agent 并发会话。
  3. 保持 at-least-once + 幂等 claim 的投递保证。

## 2. 现状（2026-08-03 实测测绘）

两套调度体系并存：

### A. 事件即时推送路径（已存在）

Feishu 入站 → FeishuConnector.HandleIncomingAsync:172（连接器层 external_message_id TTL 去重）→ MessageGatewayIngress.AcceptAsync:45 → MessageSystem.SendAsync:50 → PersistRouteAsync 去重门（MessageFabricStore.cs:30-44，按 message_id EXISTS 判断，首次返回 true）→ 仅首次发布 message.deliver 事件（MessageSystem.cs:84-110）→ MessageDeliveryDispatcher.HandleMessageDeliverAsync:101 订阅 → 非网关消息直接 claim → RuntimeDispatch 立即执行。

### B. 兜底路径

- IdleDetector（5s poll / 30s 阈值，IdleDetector.cs:219-230）→ HeartbeatOrchestrator.OnIdleTickAsync:113 → AgentWakeQueue → 心跳 SendAsync（发前检查 inbox 未 ack 心跳，有则跳过）。
- MessageDeliveryDispatcher.RunRecoveryLoopAsync:886 每 10s 扫描 message_deliveries，捡漏 queued/retrying。

### 认知纠正

"agent 间消息只能等心跳轮询"不成立——目标 Agent 空闲时，agent 间消息本来就是即时推送。真实延迟源只有三个：

| # | 延迟源 | 位置 | 影响 |
|---|--------|------|------|
| 1 | 目标 busy 时仅 RetryAsync 30s，不排队 | MessageDeliveryDispatcher.cs:225,369-384 | Agent 连续忙碌时消息响应延迟 30s 级 |
| 2 | ADR-059 网关路径 ChatExecutionWorker 2s 轮询 | ChatExecutionWorker.cs:51 | 用户消息最多 2s 延迟 |
| 3 | 空闲期心跳是唯一驱动 | HeartbeatService.cs:113-226 | token 消耗与响应延迟 |

## 3. 设计

### WP1 busy 队列 + idle 事件即时重试（核心）

- TryClaimAndDispatchAsync 遇目标 busy：保持 retrying 状态与 30s 兜底重试不变（安全网），但不再作为主路径。
- AgentExecutionStateRegistry.Complete(:40) 在 busy→idle 转换时发布 agent.availability.idle(workspaceId, agentInstanceId) 事件到 IInternalEventBus。
- MessageDeliveryDispatcher 订阅 agent.availability.idle：对该目标立即 TryClaimAndDispatchAsync，排空其 pending deliveries。
- 并发安全：claim 走既有 SQLite 事务（ClaimNextAsync），同一 delivery 不会被双抢；dispatch 入口经 AgentExecutionStateRegistry.TryBegin（CAS idle→busy）保证同一 Agent 串行——第二个竞争者被弹回，保持排队，无消息丢失。
- 注入点约束：一切发生在 message.deliver 事件之后（即 PersistRouteAsync=true 之后），与去重首次语义天然兼容。

### WP2 ChatExecutionWorker 事件化

- 2s 轮询改为 CommittedEventSignal.WaitAsync(timeout=2s)：有信号立即唤醒，2s 降级为兜底超时。最小改动，行为向后兼容。

### WP3 心跳降级为兜底巡检 + 节奏收紧

- WP1/WP2 落地后，事件驱动覆盖几乎全部唤醒需求；心跳职责收窄为：①真空闲唤醒（goal.md 驱动的巡检任务）②事件丢失兜底。
- 空闲阈值与心跳节奏放宽到更长档位（对齐夜间低频策略），节省 token。

### 风险与缓解

| 风险 | 缓解 |
|------|------|
| R1 recovery loop 与事件排空并发抢同一 agent 的不同 delivery | TryBegin CAS 串行化；败者弹回保持排队，无丢失 |
| R2 跨 conversation 并发 RuntimeDispatch 污染上下文 | 同 R1：TryBegin 保证同一 agent 同一时刻只有一个执行实例，跨会话消息串行消费 |
| R3 去重首次语义 | 注入点均在 PersistRouteAsync=true 之后，不新增事件源 |
| R4 事件总线故障导致消息永不唤醒 | recovery loop 10s + 心跳兜底不变，双保险 |

## 4. 原子任务分解（每任务一 commit，可追溯）

1. docs：本设计稿（独立提交）
2. feat(wake)：AgentExecutionStateRegistry 发布 agent.availability.idle 事件
3. feat(wake)：MessageDeliveryDispatcher 订阅 idle 事件排空 pending（含测试）
4. feat(chat)：ChatExecutionWorker 信号等待事件化（含测试）
5. chore(heartbeat)：心跳兜底定位与节奏调整（配置+文档）
6. verify：四套件测试全绿 + 重启后日志实证（即时重试延迟 <1s、零重复投递）

## 5. 验收标准

- 目标 busy 期间到达的消息，在其执行完成后 <1s 内被投递（而非 30s）。
- 零消息丢失、零重复投递（去重门不变）。
- 同一 Agent 并发会话数为零（TryBegin CAS 保证）。
- Platform/AgentTools/Host/Desktop 四套测试全绿，新增测试覆盖 idle-drain 与信号等待路径。