# QA 审阅报告 — PuddingMemoryEngine 第二轮

| 项 | 值 |
|----|-----|
| 日期 | 2026-05-05 |
| 审阅者 | QA (GPT-5.3-Codex) |
| 范围 | PuddingMemoryEngine 修复验证 + 常规审阅 |
| 结论 | **PASS** |

---

## 一、上轮问题修复验证

### P0-001 — 断言反转 ✅

**文件**: `Source/PuddingMemoryEngineTests/MemoryPersistenceTests.cs`

```csharp
Assert.IsLessThan(upperBound: 500, value: writeSw.ElapsedMilliseconds, ...);
Assert.IsLessThan(upperBound: 200, value: searchSw.ElapsedMilliseconds, ...);
```

使用命名参数 `upperBound:` 和 `value:` 明确语义，`value` < `upperBound` 逻辑正确。**已修复，无问题。**

### P1-001 — 事务包裹 ✅

**文件**: `SessionMemoryStore.cs` / `WorkspaceMemoryStore.cs`

两个 `Write` 方法均使用：
```csharp
using var tx = db.Database.BeginTransaction();
try { /* insert + evict */ tx.Commit(); }
catch { tx.Rollback(); throw; }
```

插入 + 淘汰在同一事务内，异常时完整回滚。**已修复，无问题。**

### P1-002 — Singleton DbContext ✅

**文件**: `Source/PuddingAgent/Program.cs`

仅注册了：
- `AddDbContext<PlatformDbContext>` (Singleton) — Platform 专用，非 Memory
- `AddDbContextFactory<PlatformDbContext>` (Singleton)
- `AddDbContextFactory<ControllerDbContext>` (Singleton)
- `AddDbContextFactory<MemoryDbContext>` (Singleton)

无 `AddDbContext<MemoryDbContext>` 注册。`SessionMemoryStore` / `WorkspaceMemoryStore` 构造函数接收 `IDbContextFactory<MemoryDbContext>`，每次操作 `CreateDbContext()` 短生命周期使用。**已修复，无问题。**

### P1-003 — 连接关闭 ✅

**文件**: `Source/PuddingMemoryEngine/Data/MemoryDbInitializer.cs`

```csharp
public static async Task InitializeAsync(IDbContextFactory<MemoryDbContext> dbContextFactory, ...)
{
    await using var db = await dbContextFactory.CreateDbContextAsync(ct);
    ...
}
```

使用 `IDbContextFactory` 创建 + `await using` 自动释放，无手动连接遗留。保留了重载 `InitializeAsync(MemoryDbContext db, ...)` 供测试场景直接传入。**已修复，无问题。**

---

## 二、常规审阅

| 检查项 | 结果 | 备注 |
|--------|------|------|
| 依赖方向 | ✅ | MemoryEngine 无逆向引用上层模块 |
| 异常处理 | ✅ | Write 事务完整；Recall/Clear 使用 `using var db` 短生命周期 |
| 事务原子性 | ✅ | 插入 + 淘汰在同一个 BeginTransaction/Commit/Rollback |
| DbContext 生命周期 | ✅ | 全部通过 Factory 创建，`using` 释放 |
| 测试覆盖 | ✅ | 6/6 通过（CRUD/树追踪/分支/Tag过滤/FTS5中文/性能基准） |
| 安全性 | ⚠️ | `System.Security.Cryptography.Xml 9.0.0` 存在已知高危漏洞 (NU1903)，非本次变更引入，建议后续升级 |
| 架构边界 | ✅ | 未引入新的跨层依赖 |
| 日志 | ⚠️ | SessionMemoryStore/WorkspaceMemoryStore 的 Write/Catch 无日志记录（catch 仅 rollback+rethrow），不阻断但建议后续补充 |

### 新引入问题

**无 P0/P1 问题。**

**P2-001** — `SessionMemoryStore.Write` 和 `WorkspaceMemoryStore.Write` 的 `catch` 块中事务回滚后无 `SimpleLogger` 记录。按项目规范"异常被吞掉必须记录日志"，此处异常虽继续传播但仍建议记录回滚原因以便排查。**非阻断。**

**P2-002** — NuGet 包 `System.Security.Cryptography.Xml 9.0.0` 高危漏洞警告。非本次变更引入，建议独立任务升级。**非阻断。**

---

## 三、测试结果

```
总计: 6, 失败: 0, 成功: 6, 已跳过: 0
```

全部通过，无回归。

---

## 四、结论

**PASS** — 上轮 4 个问题（1×P0 + 3×P1）均已正确修复，无新引入的 P0/P1 问题。2 个 P2 改进建议可排入后续迭代。
