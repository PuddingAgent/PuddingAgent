# Agent Status 工具 — 施工规划

## 目标
增强工作区内 Agent 的可观测性，能查看每个 Agent 的实时工作状态、心跳频率、目标状态等运行态信息。

## 方案
**不修改**现有的 `list_agents` 工具（保持名册功能），**新建**一个 `agent_status` 工具来聚合运行态信息。

---

## 工具定义

```csharp
[Tool(
    id: "agent_status",
    name: "Agent status",
    description: "查看工作区内 Agent 的运行状态：心跳频率、目标状态、队列状态、最近活动时间等。",
    category: ToolCategory.Query,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.ConcurrencySafe)]
```

### 参数

| 参数 | 类型 | 必填 | 说明 |
|------|------|------|------|
| `agent_id` | string | 否 | 指定查看某个 Agent，不传则查看全部 |

---

## 数据来源

| 数据 | 来源 | 说明 |
|------|------|------|
| **心跳频率** | `{AgentDataRoot}/{agentId}/heartbeat.json` | sleep 持久化的 min/max 值；文件不存在则使用默认 3600s |
| **目标状态** | `{AgentDataRoot}/{agentId}/goal.md` | 文件存在且有内容 = 有活跃目标 |
| **队列状态** | `AgentWakeQueue` | 该 agent 是否在唤醒队列中等待 |
| **空闲状态** | `IdleDetector` | 系统全局空闲时长 |
| **最近活动** | Agent 日志消息目录 | 最近一条消息的时间戳 |

---

## 输出格式（文本 + JSON）

### 文本格式

```
Agent Status Report
═══════════════════════════════════════════
默认助手 (通用助手)
  Agent ID:  default.global_general-assistant.823
  Status:    空闲中
  Heartbeat: active (min=120s, max=300s)
  Goal:      Code Map V0.2 (活跃)
  Last Activity: 2 分钟前
  In Queue: 是 (预计 180s 后唤醒)

dev (代码助手)
  Agent ID:  default.global_code-assistant.78ff46
  Status:    空闲中
  Heartbeat: active (默认 3600s)
  Goal:      记忆隔离 P0 (活跃)
  Last Activity: 5 分钟前
  In Queue: 是 (预计 3590s 后唤醒)

mimo (研究助手)
  Agent ID:  default.global_research-assistant.c1
  Status:    待命中
  Heartbeat: inactive (无心跳配置)
  Goal:      无
  Last Activity: 未知
  In Queue: 否
```

### JSON 格式（给 AI 解析用）

```json
[
  {
    "agent_id": "default.global_general-assistant.823",
    "name": "默认助手",
    "role": "通用助手",
    "status": "idle",
    "heartbeat": { "active": true, "min_idle_seconds": 120, "max_idle_seconds": 300 },
    "goal": { "has_goal": true, "summary": "Code Map V0.2" },
    "last_activity_minutes_ago": 2,
    "in_queue": true,
    "estimated_wake_seconds": 180
  }
]
```

---

## 改动文件清单

| 文件 | 操作 | 说明 |
|------|------|------|
| `Source/PuddingRuntime/Tools/BuiltIns/Agents/AgentStatusTool.cs` | **新建** | 工具主体，聚合所有数据源并格式化输出 |
| `Source/PuddingRuntime/Services/AgentWakeQueue.cs` | **修改** | 新增 `IsInQueueAsync(agentId)` 和 `GetWakeRequestAsync(agentId)` 方法 |
| `Source/PuddingRuntime/Models/AgentStatusReport.cs` | **新建** | 输出数据模型（可选，如果直接用匿名类型则不需要） |

---

## AgentWakeQueue 需要新增的方法

```csharp
/// <summary>检查指定 agent 是否在唤醒队列中</summary>
public Task<bool> IsInQueueAsync(string agentId, CancellationToken ct = default)

/// <summary>获取指定 agent 在队列中的唤醒请求（若存在）</summary>
public Task<WakeRequest?> GetWakeRequestAsync(string agentId, CancellationToken ct = default)
```

这两个方法需要遍历 PriorityQueue（只读），不修改队列状态。

---

## 实施步骤

1. `AgentWakeQueue.cs` — 新增 `IsInQueueAsync` 和 `GetWakeRequestAsync`
2. `AgentStatusTool.cs` — 新建工具，实现数据聚合
3. 验证：调用 `agent_status` 查看全部 Agent → 调用 `agent_status("dev")` 查看特定 Agent
