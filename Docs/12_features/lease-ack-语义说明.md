# Lease / Ack 投递语义说明

> 基于 `MessageFabricStore` 实现，文档化 `receive_messages`→`ack` 的完整状态机与重投语义。

## 概述

Pudding Message Fabric 为每条消息投递（Delivery）维护一个持久化状态机，
确保消息在 `receive_messages` / `ack` 协议下的至少一次（at-least-once）投递语义。

核心原则：
- **Lease（租约）**：消费者通过 `ClaimNextAsync` 声明一条待投递消息，获得一个有时限的独占处理权。
- **Renew（续租）**：长执行通过 `RenewLeaseAsync` 周期续租；只有当前 `executionId` 且未过期的 owner 可以续租。
- **Ack（确认）**：消费者处理完毕后调用 `AckAsync` 将投递标记为 `delivered`，释放租约。
- **过期重投**：租约到期未 ack，系统自动将投递回退到 `retrying` 状态，允许其他消费者（或同一消费者重启后）重新声明。
- **死信**：消费者可主动将投递标记为 `dead_letter`（不可恢复），或由外部策略（如超最大重试次数）触发。

## 状态机

```
                        ┌──────────┐
                        │  queued  │  ← PersistRouteAsync 创建
                        └────┬─────┘
                             │ ClaimNextAsync / ClaimBatchAsync
                             ▼
                     ┌──────────────┐
                     │  delivering  │  ← 持有 lease, LeaseUntil 有效
                     └──┬───┬───┬──┘
           AckAsync     │   │   │  DeadLetterAsync
        ┌───────────────┘   │   └──────────────┐
        ▼                   ▼                  ▼
  ┌───────────┐    ┌────────────┐     ┌──────────────┐
  │ delivered │    │  retrying  │     │ dead_letter  │
  └───────────┘    └─────┬──────┘     └──────────────┘
                         │
                         │ RecoverExpiredLeasesAsync
                         │ (LeaseUntil < now 且 status=delivering)
                         │
          ClaimNextAsync ─┘ (重新进入 delivering)
```

### 状态定义

| 状态          | 含义                                                   | 进入方式                                                     |
|---------------|--------------------------------------------------------|--------------------------------------------------------------|
| `queued`      | 新创建，等待消费者声明                                  | `PersistRouteAsync`                                          |
| `delivering`  | 已被消费者声明，持有租约（`LeaseUntil`）               | `ClaimNextAsync` / `ClaimBatchAsync`                         |
| `delivered`   | 消费者已确认处理完毕                                   | `AckAsync`                                                   |
| `retrying`    | 租约过期或消费者主动请求重试，等待下次声明              | `RecoverExpiredLeasesAsync` / `RetryAsync`                   |
| `dead_letter` | 不可恢复的失败，不再参与投递                            | `DeadLetterAsync`                                            |

### 关键字段

| 字段                    | 说明                                                              |
|-------------------------|-------------------------------------------------------------------|
| `DeliveryId`            | 投递唯一标识                                                      |
| `MessageId`             | 关联的 RoomMessage                                                 |
| `Status`                | 当前状态                                                           |
| `LeaseUntil`            | 租约到期时间（Unix 毫秒），仅在 `delivering` 状态有效             |
| `AttemptCount`          | 已尝试投递次数（每次 Claim 递增）                                  |
| `ClaimedByExecutionId`  | 声明该投递的消费者执行 ID，Ack/Retry/DeadLetter 时校验所有权      |
| `AvailableAt`           | 重试可用时间（Unix 毫秒），`retrying` 状态有效                     |
| `LastError`             | 最近一次错误信息                                                   |

## 操作语义

### 1. 消息发送 → `queued`

```csharp
// MessageSystem.SendAsync → MessageFabricStore.PersistRouteAsync
// 创建 RoomMessage + N 条 MessageDelivery（status=queued）
var persisted = await _store.PersistRouteAsync(workspaceId, plan, ct);
if (!persisted) { /* 一期去重：message_id 已存在 → 跳过 */ }
```

### 2. 接收消息 → `delivering`（加租约）

```csharp
// MessageFabricStore.ClaimNextAsync
// SELECT ... WHERE (status=queued OR status=retrying) AND AvailableAt <= now
// ORDER BY priority DESC, created_at ASC LIMIT 1 FOR UPDATE
// → SET status=delivering, attempt_count++, lease_until=now+leaseDuration,
//   claimed_by_execution_id=executionId
```

租约默认时长：`ClaimRequest.LeaseDuration`（默认 5 分钟）。

### 3. 长执行续租与 execution fencing

```csharp
// MessageFabricStore.RenewLeaseAsync
// WHERE delivery_id=... AND status=delivering
//   AND claimed_by_execution_id=executionId AND lease_until>=now
// → SET lease_until=now+leaseDuration
```

Runtime 消费者每 2 分钟为 5 分钟租约续期，并在 ACK/Retry/DeadLetter/发送回复前再次续租校验。
返回 `false` 表示租约已过期、被回收或转移；旧 runtime execution 必须取消并丢弃结果，不得产生任何投递副作用。

### 4. 确认 → `delivered`

```csharp
// MessageFabricStore.AckAsync
// WHERE delivery_id=... AND (claimed_by_execution_id 匹配)
// → SET status=delivered, ack_at=now, lease_until=NULL, claimed_by_execution_id=NULL
```

**所有权校验**：`AckAsync`、`RetryAsync`、`DeadLetterAsync` 会严格检查
`ClaimedByExecutionId`。非空 executionId 只有与当前 owner 完全一致才允许变更；租约回收把 owner
清空后，旧 executionId 也不能回写。空 executionId 只保留给显式 legacy 管理入口。

### 5. 租约过期 → `retrying`

由后台调度器周期性调用（建议间隔 ≤ 租约时长的 1/3）：

```csharp
// MessageFabricStore.RecoverExpiredLeasesAsync
// SELECT ... WHERE status=delivering AND lease_until < now
// → SET status=retrying, available_at=now, lease_until=NULL,
//   claimed_by_execution_id=NULL
```

过期投递回到 `retrying` 状态后，`ClaimNextAsync` 可再次选中。

### 6. 主动重试 → `retrying`

```csharp
// MessageFabricStore.RetryAsync
// → SET status=retrying, available_at, lease_until=NULL,
//   claimed_by_execution_id=NULL, last_error=error
```

### 7. 死信 → `dead_letter`

```csharp
// MessageFabricStore.DeadLetterAsync
// → SET status=dead_letter, lease_until=NULL,
//   claimed_by_execution_id=NULL, last_error=error
```

死信投递 **不再参与** `ClaimNextAsync` / `ListAsync` 查询。

## 消费者协议

```
consumer                                    MessageFabricStore
  │                                                │
  │ POST /api/workspaces/{ws}/receive_messages     │
  ├──────────────────────────────────────────────► │
  │                                                │ ClaimNextAsync (lease)
  │ ◄─── 200 { items: [{delivery_id, ...}] } ────  │
  │                                                │
  │ [处理消息...]                                   │
  │                                                │
  │ POST /api/workspaces/{ws}/messages/{id}/ack    │
  ├──────────────────────────────────────────────► │
  │                                                │ AckAsync
  │ ◄─── 200 OK ────────────────────────────────   │
  │                                                │
```

**正确做法**：
1. 调用 `receive_messages` 获取 `delivery_id`。
2. 长处理在租约到期前调用 `renew`，并把同一 `executionId` 贯穿到终态操作。
3. 在 ACK/Retry/DeadLetter 或外部回复前复核 ownership。
4. 调用 `ack` 确认。
5. 若处理失败且期望重试，调用 `retry`（设置 `available_at` 控制重试时机）。
6. 若不可恢复，调用 `dead_letter`。

**错误做法**：
- 获取 delivery 后不 ack → 租约过期自动重投，消息被重复消费。
- 租约过期后仍继续执行或尝试 ack → 必须被拒绝（`ClaimedByExecutionId` 已清空或已属于新 execution）。

## 与一期去重的关系

| 层级     | 机制                                        | 去重键                              | TTL       |
|----------|---------------------------------------------|--------------------------------------|-----------|
| Fabric   | `PersistRouteAsync` 返回 `false`             | `message_id`（内部）                | 永久      |
| 连接器   | 内存 `ConcurrentDictionary`（二期新增）       | `(connector_id, external_message_id)` | 2 小时    |

两层去重互补：
- Fabric 层保证同一 `message_id` 的投递只持久化一次。
- 连接器层防止外部事件重试生成不同 `message_id` 导致的重复。

## 相关源文件

- `Source/PuddingPlatform/Services/MessageFabric/MessageFabricStore.cs` — 状态机实现
- `Source/PuddingPlatform/Services/MessageFabric/MessageSystem.cs` — 发送入口
- `Source/PuddingRuntime/Services/Messaging/MessageDeliveryDispatcher.cs` — 消费者侧租约管理
- `Source/PuddingCore/Models/MessageFabricModels.cs` — 数据模型与状态常量
- `Source/PuddingHost/Connectors/FeishuConnector.cs` — 连接器层去重
