# P0-4f 第⑦步「归档删表 session_event_log」操作记录 / 回滚手册

> 生成时间：2026-08-15
> 生成原因：物理删表是不可逆操作，落此操作记录，损坏后可 fix
> 状态：施工进行中（本文档随施工进度更新）

---

## 一、基线

| 项 | 值 |
|----|----|
| 第⑦步开始前 HEAD | `3241e62909ec0dcc0975952baa6a92576b64208d` |
| 分支 | `master` |
| 语义 | 第⑥步「关双写」已落库，`EventLogDualWriteEnabled` 默认 `false` |

> 第⑦步一切删除改动都在 `3241e62` 之后提交。**回滚锚点 = `3241e62`**。

---

## 二、目标

物理删除旧流 `session_event_log` 表及其全部代码引用，使 `conversation_events` 成为唯一 canonical 事实源。

分三段落：
1. **归档**（可逆）：DROP 前导出 `session_event_log` 数据
2. **删代码**（git 可回滚）：删实体/DbSet/写路径/维护分支/测试
3. **物理 DROP**（不可逆）：bootstrap 幂等 `DROP TABLE IF EXISTS`

---

## 三、删除范围清单（分组）

### 组 1：实体与 EF 映射
- `SessionEventLogEntity` 实体文件（`Source/PuddingPlatform/Data/Entities/` 下）
- `PlatformDbContext.cs:36` `DbSet<SessionEventLogEntity>`
- `PlatformDbContext.cs:383-390` `OnModelCreating` 配置 + 3 个索引
- `PlatformDbContextModelSnapshot.cs` 移除 `SessionEventLogEntity` 块（当前快照）
- ⚠️ 2 处历史 Designer 快照（20260518 / 20260608）**保留不改**（EF 规范：历史迁移快照必然残留，不追求 grep 零残留）

### 组 2：写路径（SessionStateManager.cs）
- `AppendAsync` / `AppendBatchAsync` / `AppendSqliteEventAsync` / `PersistBufferedSessionEventsAsync`
- 双写开关 `_eventLogDualWriteEnabled` 字段 + 构造读取 + 5 处 `if` gate

### 组 3：BLOCKER 1 核心改造（plan C，SessionStateManager.cs）
- C1 `AppendAsync` 去掉 `new SessionEventLogEntity`，直建 `SessionEventEnvelope`（`EventId = draft.EventId ?? eventId(GUID)`，修复恒 `"0"` bug）
- C2 `MapToEnvelope` 改签名 `(draft, trace, seq, recordedAt, eventId, workspaceId)` 或内联
- C3 `AppendBatchAsync` 死代码直接删（含接口 `ISessionEventWriter.cs:42`）
- C4 删整个 `if(_eventLogDualWriteEnabled)` 双写块
- C5 清理 `_eventLogDualWriteEnabled` 字段/读取/5 处 gate

### 组 4：维护分支与保护列表
- `RetentionPruningService.cs` 摘除 `"session_event_log"` 白名单/归档集（避免 `no such table`）
- `SessionApiController.cs:261` `ExecuteDeleteAsync` 删旧表行
- `StorageMaintenanceService` `ProtectedEventTables` + Description 摘除 `session_event_log`

### 组 5：孤儿修复（BLOCKER 4，一并修）
- `DeletePlatformSessionArtifactsAsync` 补 `db.ConversationEvents.Where(c => c.ConversationId == sessionId).ExecuteDeleteAsync(ct)`（当前漏删 canonical，属孤儿）

### 组 6：配置与文案
- `appsettings.json` 摘除旧表相关配置
- ~20 处注释/文案（含 `ISessionEventWriter` / `SessionEventDraft` 过时 doc 文字）

### 组 7：测试
- `DiagnosticRetentionServiceTests` / `RetentionArchiveWriterTests` / `RetentionPruningServiceTests`
- `MessageApiControllerTests:278` / `SessionApiControllerTests:280`
- 其他引用旧表实体的测试桩

---

## 四、施工顺序与每步验证

| 序 | 动作 | 验证 | 可逆性 |
|----|------|------|--------|
| 0 | 定位真实 DB 路径 + 归档导出（见 §五） | 行数核对一致 | ✅ 可逆 |
| 1 | plan C 改造 SessionStateManager（去实体引用） | `dotnet build` 0 错误 | ✅ git |
| 2 | 删 DbSet/实体/OnModelCreating/快照 | build 0 错误 | ✅ git |
| 3 | 删维护分支/保护列表/孤儿修复 | build 0 错误 | ✅ git |
| 4 | 删测试/文案/配置 | `dotnet test` 通过 | ✅ git |
| 5 | bootstrap 幂等 DROP 脚本 | build 0 错误 | ✅ git |
| 6 | **物理 DROP**（重启自动执行） | 表消失、canonical 正常 | ❌ **不可逆** |

> 第 6 步物理 DROP 前，**必须**再次向用户显式确认。

### 红线（禁止触碰）
- 不改 `ReserveSequenceAsync`（序列连续性）
- 不改 `AgentExecutionService.Streaming.cs`（只用 `SessionEventDraft`/`Envelope`，不引用实体）
- 不碰 `conversation_events` 写入逻辑
- 不删 `ISessionEventWriter` 接口与 `SessionEventDraft` 契约（仅删过时 doc 文字）

---

## 五、归档策略（DROP 前必做）

1. **定位真实 DB**：通过 `appsettings.json` 连接字符串 / 运行时 DataRoot 找到真实 `pudding_platform.db`（注意：仓库根 `data/pudding_platform.db` 是 0 字节占位，非真实库）。
2. **整库副本**：`cp pudding_platform.db data/backup/pudding_platform.db.pre-drop-20260815.bak`
3. **表级导出**：`sqlite3 pudding_platform.db ".dump session_event_log" > data/backup/session_event_log-20260815.sql`
4. **行数校验**：`SELECT COUNT(*) FROM session_event_log;` 记录行数，与导出 SQL 的 INSERT 条数核对一致。
5. 归档文件路径记录于本手册下方「归档产物」节。

### 归档产物（待施工时填写）
| 文件 | 路径 | 校验 |
|------|------|------|
| 整库副本 | `data/backup/pudding_platform.db.pre-drop-20260815.bak` | — |
| 表级 SQL | `data/backup/session_event_log-20260815.sql` | 行数：____ |

---

## 六、回滚手册（损坏后 fix）

### 6.1 代码回滚（git 可回滚，最常用）
```bash
# 回滚到第⑦步开始前（丢弃全部删表改动）
git reset --hard 3241e62909ec0dcc0975952baa6a92576b64208d

# 或只撤销某个中间 commit（保留后续）
git revert <bad-commit-sha>

# 查看第⑦步产生了哪些 commit
git log --oneline 3241e62..HEAD
```

### 6.2 数据回滚（物理 DROP 后恢复数据）
```bash
# 用归档 SQL 回灌 session_event_log
sqlite3 pudding_platform.db < data/backup/session_event_log-20260815.sql

# 或整库回退（若 DROP 后未再写其他表，直接覆盖）
cp data/backup/pudding_platform.db.pre-drop-20260815.bak pudding_platform.db
```

### 6.3 双写回滚（若切流后发现问题）
```bash
# 第⑥步已把 EventLogDualWriteEnabled 默认值 true→false（commit 3241e62）
# 若需临时恢复双写观察，改回 true（或配置覆盖），重建实体/DbSet
git show 3241e62 -- Source/PuddingPlatform/Services/SessionStateManager.cs
```

### 6.4 提交链（第⑦步之前，回滚锚点向下）
```
3241e62  第⑥步 关双写 EventLogDualWriteEnabled true->false  ← 回滚锚点
6538ac4  第⑥步 死代码清理（6 读端口）
2b39261  第⑥步 逃逸分支 maxSeq 改源
38e84d4  A组 废弃 consistency 端点
5f4d35b  ChatOmni 物理删除
126ba13  C组 benchmark 迁 canonical
07bf40b  B3 trace-report 迁 canonical
fd42470  B2 RuntimeTimeline 换源
9c07820  B1 共享投影器
9343a7c  D组 token rebuild 迁 canonical
f14e0f0  C5 /replay 退役
e148fff  C4 会话目录投影切流
383716a  C3 conversation_catalog 回填
cfb0c31  C2 Projector UPSERT
2716086  C1 conversation_catalog 表落地
660dcf9  SubAgentManager 帧写删除
0db49bd  W4 死代码
4d76174  W3 去重
a1f0d1c  余额查询
```

---

## 七、物理 DROP 不可逆警告

- 第 6 步物理 `DROP TABLE` 一旦执行，旧流数据仅存于归档文件（§五）。
- **执行前必须**：① 归档产物校验通过；② 用户显式确认；③ 记录执行时间与执行人。
- 若 DROP 后需要旧流数据，走 §6.2 回灌。

---

## 八、施工状态（随进度更新）

| 段 | 状态 | 完成时间 | commit |
|----|------|---------|--------|
| 0 归档 | ⏳ 待 | — | — |
| 1 plan C 改造 | 🔄 在途（sub-fdc61ca8）| — | — |
| 2 删实体/DbSet | ⏳ 待 | — | — |
| 3 删维护分支/孤儿 | ⏳ 待 | — | — |
| 4 删测试/文案 | ⏳ 待 | — | — |
| 5 bootstrap DROP | ⏳ 待 | — | — |
| 6 物理 DROP | ⏳ 待（需显式确认）| — | — |
