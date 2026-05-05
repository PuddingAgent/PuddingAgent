# QA 审阅报告 — PuddingMemoryEngine Phase 1 持久化改造

| 字段 | 值 |
|------|-----|
| 审阅日期 | 2026-05-05 |
| 审阅模型 | Claude Sonnet 4.6 |
| 开发者 | dahuang |
| 审阅范围 | MemoryEngine Phase 1 持久化（EF Core SQLite + FTS5） |
| 测试结果 | 6 passed / 0 failed（测试通过，但含 P0 逻辑错误） |
| **最终结论** | **FAIL** |

---

## 问题列表

### P0 — 阻断（必须修复后重审）

#### P0-001：性能基准测试断言参数顺序完全反转

**文件**：`Source/PuddingMemoryEngineTests/MemoryPersistenceTests.cs` L331–334

```csharp
// 当前（错误）：检查 500 < elapsed，性能越差越容易 PASS
Assert.IsLessThan(500, writeSw.ElapsedMilliseconds,
    $"100 条消息写入耗时 {writeSw.ElapsedMilliseconds}ms，超出 500ms 阈值。");
Assert.IsLessThan(200, searchSw.ElapsedMilliseconds,
    $"FTS 查询耗时 {searchSw.ElapsedMilliseconds}ms，超出 200ms 阈值。");

// 应为（正确）：检查 elapsed < 500，性能越好越容易 PASS
Assert.IsLessThan(writeSw.ElapsedMilliseconds, 500, ...);
Assert.IsLessThan(searchSw.ElapsedMilliseconds, 200, ...);
```

**确认依据**：
- MSTest4 `Assert.IsLessThan(left, right)` 断言 `left < right`。
- 项目内其他用例一致：`Assert.IsLessThan(result.RetainedLines, 50)` 检查实际值 < 上限。
- 本次测试耗时 953ms（远超 500ms），断言以 `500 < 953` 为 true 而通过——**测试 PASS 正是因为性能超标**，误报绿灯。
- 该测试完全失去"性能回归防护"意义，必须修复。

---

### P1 — 严重（当前版本可接受，但须在 Phase 2 前解决）

#### P1-001：`Write` 方法的两阶段写入缺少显式事务

**文件**：`SessionMemoryStore.cs` L47–66 / `WorkspaceMemoryStore.cs` L47–66

```csharp
using var db = _dbContextFactory.CreateDbContext();
db.Memories.Add(...);
db.SaveChanges();           // ← 第 1 次保存（记录已入库）

// 若在此处进程崩溃，溢出记录不会被清理
var overflowIds = db.Memories.Where(...).Skip(Max).Select(...).ToList();
if (overflowIds.Count > 0)
{
    db.Memories.RemoveRange(...);
    db.SaveChanges();       // ← 第 2 次保存（清除溢出）
}
```

**风险**：两次 `SaveChanges` 不在同一事务，进程中途崩溃将留下超出 `MaxEntriesPerSession/Workspace` 限制的数据。建议用 `db.Database.BeginTransaction()` 包裹整个写入+清理逻辑。

#### P1-002：同时注册 Singleton DbContext 和 DbContextFactory

**文件**：`Source/PuddingAgent/Program.cs` L163–167

```csharp
// 危险：DbContext 不是线程安全的，Singleton 生命周期会导致并发访问冲突
builder.Services.AddDbContext<MemoryDbContext>(opt => { ... }, ServiceLifetime.Singleton);
// 正确：Singleton Factory 每次操作创建独立 DbContext
builder.Services.AddDbContextFactory<MemoryDbContext>(opt => { ... }, ServiceLifetime.Singleton);
```

**风险**：`AddDbContext<MemoryDbContext>(Singleton)` 将单例 `MemoryDbContext` 注入 DI 容器，任何地方注入后在并发场景下会导致数据损坏或竞态。虽然当前代码路径均走 Factory，但此注册隐患随时可能被误用。建议删除 `AddDbContext<MemoryDbContext>` 那行，只保留 `AddDbContextFactory`。

#### P1-003：`TriggerExistsAsync` 手动打开连接后未关闭

**文件**：`Source/PuddingMemoryEngine/Data/MemoryDbInitializer.cs` L90–101

```csharp
var connection = db.Database.GetDbConnection();
if (connection.State != ConnectionState.Open)
{
    await connection.OpenAsync(ct);   // ← 手动打开
}
await using var command = connection.CreateCommand();
// ... 使用完毕后未 CloseAsync
// 依赖 DbContext.Dispose() 来最终关闭连接
```

**风险**：EF Core 约定：手动打开的连接由调用方负责关闭。若 `db.Dispose()` 因异常未能调用，连接将泄漏。建议在使用后显式 `connection.CloseAsync()` 或将检查改为直接使用 `db.Database.OpenConnectionAsync()` + `db.Database.CloseConnection()`。

---

### P2 — 改进（建议但不阻断）

#### P2-001：`HasDefaultSchema("memory")` 在 SQLite 中无效，造成误导

**文件**：`MemoryDbContext.cs` L22

SQLite 不支持 schema 命名，EF Core 会静默忽略此配置。当前不产生错误，但若将来迁移到 PostgreSQL，`memory.Sessions`、`memory.Messages` 表名会突然生效，可能打破现有查询和外键。建议删除此行，或加注释说明"此配置对 SQLite 无效，保留为迁移到 PG 时的占位"。

#### P2-002：FTS5 虚拟表及触发器在 SQL 文件与初始化器中双重定义

**文件**：`init_memory.sql` / `MemoryDbInitializer.EnsureFtsArtifactsAsync`

`init_memory.sql` 已包含 `CREATE VIRTUAL TABLE IF NOT EXISTS Messages_fts` 及三个 `CREATE TRIGGER IF NOT EXISTS`。`EnsureFtsArtifactsAsync` 重复创建同样的对象（通过 `TriggerExistsAsync` 幂等保护）。冗余逻辑增加维护负担：修改触发器时需同步两处。建议删除 SQL 文件中的 FTS5 部分，统一由初始化器管理。

#### P2-003：`SearchMessagesAsync` 未捕获 FTS5 语法错误

**文件**：`MemoryEngine.cs` L156–175

当用户传入包含 FTS5 特殊字符（`"`, `(`, `)`, `*`, `-`）的查询时，SQLite 会抛出 `SqliteException: fts5: syntax error near...`，该异常未被处理，将直接传播到调用方。建议在方法内 `catch (SqliteException)` 并 fallback 到纯文本 LIKE 查询或返回空列表，同时记录警告日志。

#### P2-004：`BranchType` 使用魔法字符串，缺少编译时约束

**文件**：`MessageEntity.cs` / 测试代码

`"MAIN"`, `"RETRY"` 等分支类型散落在多处，任何一处拼写错误（如 `"main"`, `"Retry"`）都不会在编译时报错。建议定义 `static class BranchTypes { public const string Main = "MAIN"; ... }` 或枚举。

#### P2-005：测试覆盖空白

以下关键路径缺乏直接测试：
- `SessionMemoryStore.Write/Recall/Clear` DB 模式的整体链路（当前测试只直接操作 `DbContext`）
- `WorkspaceMemoryStore.Recall(tag)` 标签过滤
- `MemoryDbInitializer.InitializeAsync` 幂等性（多次调用不报错）
- `MemoryEngine.WriteBack` 解析 `REMEMBER[tag]: content` 标记
- `SearchMessagesAsync` 传入无效 FTS5 表达式的异常行为

#### P2-006：依赖 `System.Security.Cryptography.Xml 9.0.0` 存在已知高危漏洞

构建警告：
```
NU1903: 包 "System.Security.Cryptography.Xml" 9.0.0 具有已知的 高 严重性漏洞
https://github.com/advisories/GHSA-37gx-xxp4-5rgx
https://github.com/advisories/GHSA-w3x6-4m5h-cxqf
```

此为传递依赖，需在 `PuddingMemoryEngine.csproj` 中添加 `<PackageReference Include="System.Security.Cryptography.Xml" Version="9.0.x" />` 强制升级（待确认安全版本号）。

---

## 正向确认（通过项）

| 检查项 | 结论 |
|--------|------|
| SessionMemoryStore / WorkspaceMemoryStore 公开接口完全不变 | ✅ |
| Null factory → 内存 fallback 正确处理 | ✅ |
| Singleton 服务使用 IDbContextFactory（每次操作创建新 DbContext）| ✅ |
| FTS5 tokenize='trigram' 支持中文搜索 | ✅（测试通过） |
| MessageEntity.ParentId 自引用 + OnDelete.Restrict | ✅ |
| CompactedBy 外键 + OnDelete.Restrict | ✅ |
| SQL 注入：FTS5 MATCH 使用 SqliteParameter 参数化 | ✅ |
| DbContext 通过 using 正确释放（Store/测试辅助类）| ✅ |
| WAL 模式 + foreign_keys 在 InitializeAsync 中设置 | ✅ |
| 级联删除：Session 删除时消息级联清理 | ✅ |
| 生产环境 JWT Key 校验（MUST-CHANGE 检查）| ✅ |
| DI 注册启动初始化路径正确（IDbContextFactory → MemoryDbInitializer）| ✅ |

---

## 修复建议优先级

| 优先级 | 问题 | 建议改法 |
|--------|------|---------|
| P0 | P0-001 断言参数反转 | 互换两处 `Assert.IsLessThan` 的参数顺序 |
| P1 | P1-001 Write 缺事务 | `db.Database.BeginTransaction()` 包裹两次 SaveChanges |
| P1 | P1-002 Singleton DbContext | 删除 `AddDbContext<MemoryDbContext>(Singleton)` 注册 |
| P1 | P1-003 连接未关闭 | 使用后显式调用 `connection.CloseAsync()` |
| P2 | P2-001 HasDefaultSchema | 删除或加注释 |
| P2 | P2-003 FTS5 异常未处理 | catch SqliteException + 日志 |
| P2 | P2-006 漏洞包 | 强制升级版本号 |

---

## 结论

**FAIL** — 存在 1 个 P0 阻断问题（性能基准测试断言完全反转，测试通过为假阳性）和 3 个 P1 严重问题。请开发者修复后重新提交审阅。
