# P0-4f 第⑥步门禁读者迁移 — 完整推进日志归档

> 归档时间：2026-08-15 20:31
> 来源：goal.md（精简前）
> 范围：P0-4f 第⑥步门禁「旧流 session_event_log 读者清零」的完整调研、拍板、施工、验收记录

## 一、读者全景（门禁验证结论）

穷举 Source 目录 1528 个 .cs 文件后，旧流 `session_event_log` 生产活跃读者共 4 组 + 1 维护型 + 死代码：

| 组 | 端点/服务 | 迁移处置 |
|----|----------|---------|
| A | consistency + trace-report | consistency 废弃；trace-report 迁共享投影器 |
| B | 诊断时间线 3 端点 | 迁 canonical（共享投影器） |
| C | benchmark 评估 | 迁 canonical（主从反转） |
| D | token rebuild | 迁 canonical（invocationIndex 配对） |
| E | RetentionPruningService（6h 后台）| 保留到旧表排空，第⑦步删表时同步删 |
| 死代码 | SessionStateManager 6 读端口 | 直接清理 |

## 二、用户拍板（2026-08-15 17:43）核心结论

- B/C 全部迁 canonical，**不保留 legacy-only 运行模式**
- 诊断与 benchmark 是面向未来运行的核心能力，冻结旧流会让新会话永久失去 E2E 证据和评估能力
- 旧流逐 token/frame 粒度不适合做稳定 benchmark 口径
- 阶段①做数据样本和覆盖率验证，不再讨论"能不能迁"

### B 组迁移口径
- 新增共享 `ConversationDiagnosticEventProjector`，trace-report/RuntimeTimeline/E2E 共用，**禁止三服务分别解析 Payload**
- 映射：Id=EventId / Kind=conversation_event / Component=ProducerComponent??SourceKind??"conversation" / Operation=Type / SessionId=ConversationId / StartedAt=OccurredAt / Status=显式事件类型表映射
- E2E 四源：RuntimeActivity + EventQueue + ConversationEvent + SubAgentRun
- 关联优先级 TraceId → RunId → TurnId/CommandId，禁止按时间邻近猜测

### C 组迁移口径
- canonical Conversation Event 主证据源；JSONL 仅可选诊断附件
- 工具调用按稳定 callId 配对，不再按工具名 Queue 猜测
- Usage 读 usage.recorded；失败读 tool.call.failed/turn.failed/error.recorded
- benchmark 统计领域操作数量，不统计 delta 分块数量
- DTO HasSessionEventLog → HasConversationEvents

### 其他组
- A consistency 废弃；trace-report 迁共享投影器
- D token rebuild 按 TraceId/RunId/invocationIndex/provider/model 关联 usage.recorded
- E retention 保留到旧表排空，删表时同步删 retention 分支
- 死代码直接清理，不保留兼容 Facade

## 三、迁移执行顺序（后续不再等产品裁定）

```
D(小·孤立) → B(诊断时间线投影器) → C(benchmark) → A(trace-report + consistency废弃)
→ 死代码清理(SessionStateManager 6读端口 + 逃逸分支maxSeq)
→ ⑥关双写 → ⑦归档删表(需显式确认)
```

## 四、阶段①数据样本与覆盖率验证结论

- canonical 数据充足：930,579 行 / 110 conversation / 1388 turn
- 事件类型覆盖 31/45，缺 14 canonical + 2 非标准（context/terminal）
- usage.recorded v1/v2 并存：v2=93.5%（含 provider/model/role/invocationIndex），v1=6.5%（2026-07-18 早期，缺归因字段）
- tool.call.requested/completed 无稳定 callId，配对靠 turn_id+工具名+sequence 相邻；tool.call.failed=0（失败折叠进 completed.error）
- 四源关联正确键 = session_id(=conversation_id) + run_id，非 trace_id（缺列）非 correlation_id（命名空间不一致）

### schema 漂移澄清（重要）
阶段①报告称"生产库 conversation_events 缺 4 列（trace_id/agent_id/source_kind/producer_component）是迁移前必先修的阻塞项"。
**实际判定：这是部署问题而非代码缺口**：
- INSERT 语句（L117-119）确实引用这 4 列
- EnsureTableAsync（L399）已含 CREATE TABLE IF NOT EXISTS + EnsureColumnAsync 幂等补列（L461-464）
- EnsureColumnAsync（L477）：PRAGMA table_info 检查 + ALTER TABLE ADD COLUMN 幂等
- AppendAsync 开头 L40 先 EnsureTableAsync → 顺序安全：先补列再 INSERT
- **修复 = 重启 PuddingAgent 进程**，首次启动自动补列，零代码改动

## 五、施工验收记录（逐 commit）

| 步骤 | commit | 内容 | 验证 |
|------|--------|------|------|
| D token rebuild | `9343a7cb` | gateway usage 配对迁 canonical（invocationIndex 精确配对） | 编译 0 错 + 测试 4/4 |
| B1 投影器 | `9c07820` | ConversationDiagnosticEventProjector 本体 + DI + 14 测试 | 编译 0 错 + 14/14 |
| B2 时间线换源 | `fd42470` | RuntimeTimelineQueryService 第3源 SessionEventLog→ConversationEvent | 编译 0 错 + 16/16 |
| B3 trace-report | `07bf40b3` | GetTraceReportAsync 迁 canonical + 共享投影器 + 删死代码 | 编译 0 错 + 13/13 |
| C benchmark | `126ba13` | SessionBenchmarkDiagnosticsService 主从反转 + 换读源 + HasConversationEvents | 编译 0 错 + 5/5 |
| ChatOmni 物理删除 | `5f4d35b` | git rm 两 0 字节文件 | — |
| A consistency 废弃 | `38e84d4` | 删 GetConsistency + CheckConsistencyAsync + record + fake 类 | 编译 0 错 + 466/466 |
| 逃逸分支改源 | `2b39261` | 2 处 maxSeq 从 SessionEventLogs→ConversationEvents | 编译 0 错 |

## 六、死代码清理调研结论（sub-4180393c）

SessionStateManager.cs 读 session_event_log 的：
- **6 个死代码读端口**（零生产调用者，可删）：
  - GetEventsAsync（:492）
  - GetHeadAsync（:545）
  - ReadAfterAsync
  - ReadBeforeAsync
  - GetEventCountAfterAsync
  - GetLatestSequenceNumAsync
- **2 处逃逸分支 maxSeq**（活代码，已改源到 canonical）：
  - AppendSqliteEventAsync else 分支（:1191）
  - ReserveSequenceAsync（:1310）

## 七、关键教训（工具）

1. CMD 下 `git commit -m` 带空格会被拆分（pathspec 报错），必须用 `-F` 消息文件
2. `git grep` 用 `-e` 多模式 + 明确路径，避免 `-- "*.cs"` 通配符在 CMD 下失效
3. findstr 假阴性（无匹配/引号剥离），判断编译成败必须读重定向日志尾部
4. `git_add` 参数是 `files`（字符串数组）不是 `path`
5. OpenCode glm-5.2 包月额度耗尽时报 GoUsageLimitError，需降级 deepseek-v4-pro
6. terminal 工具可用；shell 工具失败常是 CMD 语法问题（for 循环等），改用 terminal_start/wait
