# QA 报告 — PuddingMemoryEngine Phase 1 收尾 (T1.3/1.6/1.8)

| 字段 | 值 |
|------|-----|
| 日期 | 2026-05-05 |
| QA 模型 | GPT-5.3-Codex |
| 审阅范围 | T1.3 双写落盘、T1.6 上下文窗口改造、T1.8 回填脚本 |
| 结论 | **PASS_WITH_NOTES** |

---

## 1. 编译 & 测试

| 项目 | 结果 |
|------|------|
| `dotnet build PuddingPlatform` | ✅ 通过（0 error） |
| `dotnet build PuddingRuntime` | ✅ 通过（0 error） |
| `dotnet test PuddingWebApiTests` | ✅ 16/16 通过 |
| `dotnet test PuddingMemoryEngineTests` | ✅ 6/6 通过 |

> 注意：任务要求 22/22 测试通过，实际 WebApiTests 16 + MemoryEngineTests 6 = 22 ✅

**NU1903 警告**：`System.Security.Cryptography.Xml 9.0.0` 存在已知漏洞（GHSA-37gx-xxp4-5rgx, GHSA-w3x6-4m5h-cxqf）。非本次变更引入，但建议后续升级。

---

## 2. ChatApiController 双写 (T1.3)

### 2.1 IDbContextFactory 注入 ✅

- 构造函数注入 `IDbContextFactory<MemoryDbContext> memoryDbFactory`，已注册为 Singleton（`Program.cs` L158）。
- `DualWriteToMemoryDbAsync` 内通过 `await memoryDbFactory.CreateDbContextAsync(ct)` 获取短生命周期 DbContext，每次操作独立实例，无跨请求共享问题。

### 2.2 双写失败不影响主流程 ✅

- `PersistMessagesAsync` 中 `DualWriteToMemoryDbAsync` 调用被 try-catch 包裹，异常仅 `LogWarning`，不传播。
- 流式路径（`SendMessageStream`）通过 `Task.Run` fire-and-forget 调用 `PersistMessagesAsync`，且有独立 try-catch。
- **P2 注意**：`Task.Run` 内使用 `CancellationToken.None`，意味着双写不可被外部取消。这是有意为之（fire-and-forget 语义），但若 DB 写入卡住，将无取消机制。

### 2.3 Session Upsert 逻辑 ✅

- 先 `FindAsync(sessionId)`，存在则更新 `LastActivityAt`/`MessageCount`/`WorkspaceId`，不存在则 new + Add。
- 已存在 Session 时回填 `WorkspaceId` 是合理的防御逻辑。
- **P2 建议**：`MessageCount` 自增逻辑在并发场景下可能偏大（多个请求同时 +1/+2），但对展示用途无实际影响，后续可改用 SQL 原子 COUNT。

### 2.4 MessageId 冲突风险

- 格式：`{now:x}-{Guid:N[..8]}`，即十六进制时间戳 + 8 hex 随机字符。
- `MessageEntity.MessageId` 标记 `[Key] [MaxLength(32)]`，当前格式长度约 16+1+8=25 字符，在限制内。
- **P2 风险**：8 hex 随机 = 32 bit 熵，同一毫秒内约 4 billion 分之一概率冲突。对于当前业务量可接受，但若未来高并发，建议扩展随机部分到 16 字符（64 bit）。

### 2.5 双写路径的 Sequence 计算 ✅

- `maxSequence` 通过 `MaxAsync` 查询当前最大值，+1/+2 递增。在并发场景下存在竞态（两个请求同时读到相同 maxSequence），但 SQLite 单写锁会在 `SaveChangesAsync` 时报唯一约束冲突 → 被 try-catch 捕获 → 仅 LogWarning。
- **P1 风险**：并发双写导致 Sequence 重复时，虽然 catch 不影响主流程，但**消息会丢失**（一条 user+assistant 对无法写入 memory db）。建议在 DualWrite 方法内对 `DbUpdateException` 做重试或 Sequence 回查。

---

## 3. AgentExecutionService 上下文窗口改造 (T1.6)

### 3.1 可选注入 ✅

- 构造函数参数 `IDbContextFactory<MemoryDbContext>? memoryDbFactory = null`，可选注入。
- `_memoryDbFactory is null` 时所有 DB 路径自动 fallback 到内存 `_histories`。
- DI 注册在 `PuddingAgent/Program.cs`，非 Runtime 自身 Program.cs，符合 Host 组合根模式。

### 3.2 BuildContextFromDbAsync token 估算 ✅

- `1 token ≈ 3 字符`（`content.Length / 3`），最少 1 token。
- 对于中英混排是合理折中（中文 1 字 ≈ 1-2 token，英文 1 word ≈ 1-4 token ≈ 4-20 字符）。
- 取最新 100 条消息倒序 → 再正序组装，保证时间顺序正确。
- **P2 注意**：`CompactedBy == null` 过滤条件正确跳过已压缩消息，但 `ToolCalls` 和 `ToolResult` 未还原到 `ChatMessage.ToolCalls`/`ChatCallId`，意味着 DB 恢复的历史在 function-call 多轮场景下可能丢失工具调用上下文。

### 3.3 流式 vs 同步路径行为差异 ✅

- **流式路径** (`ExecuteStreamAsync`)：`TryHydrateStreamHistoryFromDbAsync` 先尝试 DB 填充 → `TrimHistoryAsync(preferDbContextWindow: true)` 优先 DB 窗口。
- **同步路径** (`ExecuteAsync`)：`TrimHistoryAsync(preferDbContextWindow: false)` → 走 legacy `TrimHistoryFallback`。
- 设计意图清晰：流式路径面向 Chat UI，需要跨重启恢复上下文；同步路径保持内存行为不变。

### 3.4 TryHydrateStreamHistoryFromDbAsync ✅

- `history.Clear()` + `AddRange` 替换，不残留旧数据。
- `Where(m => m.Role != ChatRole.System)` 排除 DB 中的 system prompt，因为调用方会重新注入 `streamingSystemPrompt`。
- catch 异常仅 LogWarning，不破坏现有 history。

### 3.5 TrimHistoryAsync ✅

- `preferDbContextWindow: true` 时尝试 DB 重建；失败 fallback 到 `TrimHistoryFallback`（保留 system + 最近 40 条）。
- DB 重建时保留 system prompt（`FirstOrDefault` + 重新插入），逻辑正确。

### 3.6 **P1 问题：TryHydrateStreamHistoryFromDbAsync 会清空内存中正在进行的工具调用状态**

流式路径在 session 首次进入时调用 `TryHydrateStreamHistoryFromDbAsync`，会 `history.Clear()` 再从 DB 重建。但 DB 中仅存储了 `Content/ThinkingJson/Role`，**丢失了 `ToolCalls` 和 `ToolCallId`**。如果 Agent 正在进行多轮工具调用（assistant 带 tool_calls → tool result → 下轮 LLM），DB 恢复的历史将无法正确传递工具调用结构给 LLM，导致 LLM 无法续接 tool 结果。

**影响范围**：当前流式路径的 system prompt 明确要求 Agent 输出纯 Markdown（`BuildStreamingSystemPrompt`），不走 function-call 闭环，因此**暂无实际影响**。但若未来流式路径启用 function-call，此问题将显现。

---

## 4. 回填脚本 (T1.8)

### 4.1 逻辑正确性 ✅

- Session INSERT OR IGNORE + 消息 INSERT OR IGNORE，幂等。
- Session 统计二次校准（Step 3）确保 MessageCount/LastActivityAt 与实际 Messages 一致。
- 事务包裹（BEGIN TRANSACTION / COMMIT），失败回滚。

### 4.2 CreatedAt 单位转换 ✅

- `CASE WHEN cm.CreatedAt < 1000000000000 THEN cm.CreatedAt * 1000 ELSE cm.CreatedAt END` — 阈值 10^12 区分秒/毫秒，逻辑正确。

### 4.3 Role 映射 ✅

- `agent → assistant`，`assistant → assistant`，`system → system`，`tool → tool`，其余 → `user`。
- 与 `ParseChatRole` 映射一致。

### 4.4 幂等性 ✅

- `INSERT OR IGNORE` 保证重复执行不重复插入。
- Step 3 UPDATE 是覆盖式校准，幂等。

### 4.5 **P2 问题：MessageId 使用 `randomblob(16)` 生成 32 hex 字符**

- `lower(hex(randomblob(16)))` 生成 32 字符 hex，与 `[MaxLength(32)]` 刚好匹配。
- 但 `DualWriteToMemoryDbAsync` 中 MessageId 格式为 `{timestamp:x}-{guid[..8]}`（约 25 字符），两种格式不统一。
- 不影响功能（都是合法 PK），但影响人工排查时的一致性预期。

### 4.6 SQL 注入风险 ✅

- 脚本中唯一的外部输入 `__MEMORY_DB_PATH__` 在 PowerShell 侧做了单引号转义（`Replace("'", "''")`），然后通过 ATTACH DATABASE 注入。
- 其他所有查询都是静态 SQL + 列引用，无拼接用户输入。
- **风险极低**。

### 4.7 **P2 问题：Session 回填时 WorkspaceId/AgentId 为空字符串**

- SQL 中硬编码 `'' AS WorkspaceId, '' AS AgentId`，因为旧 `ChatMessages` 表不包含这些字段。
- 这意味着回填的 Session 永远无法按工作区检索。已在 `DualWriteToMemoryDbAsync` 中做了回填逻辑（`if string.IsNullOrWhiteSpace(session.WorkspaceId)` → 回填），后续双写会逐步补全。

---

## 5. 安全审查

| 检查项 | 结果 |
|--------|------|
| SQL 注入 | ✅ 无风险（参数化 / 静态 SQL / 路径转义） |
| IDbContextFactory 生命周期 | ✅ Singleton Factory + 每操作 CreateDbContextAsync |
| 异常泄露 | ✅ 双写异常仅 LogWarning，不暴露给客户端 |
| NU1903 依赖漏洞 | ⚠️ `System.Security.Cryptography.Xml 9.0.0` 已知漏洞，非本次引入 |

---

## 6. 问题汇总

| 编号 | 严重度 | 模块 | 描述 | 建议 |
|------|--------|------|------|------|
| Q-01 | P1 | ChatApiController | 并发双写 Sequence 竞态可导致消息丢失 | 对 `DbUpdateException` 重试或回查 Sequence |
| Q-02 | P1 | AgentExecutionService | DB 恢复历史丢失 ToolCalls/ToolCallId，未来流式启用 function-call 时会断裂 | 在 `BuildContextFromDbAsync` 中还原 `ToolCallsJson` → `ToolCalls`、`ToolResultJson` → `ToolCallId` |
| Q-03 | P2 | ChatApiController | MessageId 随机部分仅 32 bit 熵 | 扩展到 16 字符（64 bit） |
| Q-04 | P2 | ChatApiController | fire-and-forget 双写使用 `CancellationToken.None`，DB 卡住不可取消 | 考虑传入带超时的 `CancellationTokenSource` |
| Q-05 | P2 | 回填脚本 | Session 回填 WorkspaceId/AgentId 为空 | 已有运行时回填机制，可接受 |
| Q-06 | P2 | 回填脚本 | MessageId 格式与双写不一致（32hex vs 时间戳-随机） | 统一 ID 生成策略 |
| Q-07 | P2 | PuddingMemoryEngine | NU1903 依赖漏洞 | 升级 `System.Security.Cryptography.Xml` |

---

## 7. 结论

**PASS_WITH_NOTES** — 核心功能实现正确，编译通过，测试全部通过。2 个 P1 问题当前不产生实际影响（Q-01 在低并发下极少触发；Q-02 受限于流式路径不使用 function-call），但建议在 Phase 2 启用前修复，避免积累技术债。
