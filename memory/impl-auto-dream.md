# 实施规约：Auto-Dream（定期记忆整理）

> 版本：v1.0 | 日期：2026-07-11
> 借鉴来源：Claude Code 的 Auto-Dream + Hermes Agent 的 Autonomous Curator
> 原则：增强现有 SubconsciousOrchestrator，新增 AutoDreamAsync 方法

---

## 一、现有基础设施总览（复用清单）

```
SubconsciousOrchestrator（PuddingMemoryEngine）
  ├─ 已有 IMemoryLibrary    → 完整 CRUD（Book/Chapter/Pointer/Tree）
  ├─ 已有 IMemoryLibrarian  → IngestExperienceAsync / PlanTreeMaintenanceAsync
  ├─ 已有 IMemoryLlmClient  → Flash LLM 低延迟调用
  ├─ 已有 ConsolidateAsync  → Background Extractor 已增强
  ├─ 已有 ExtractionPayload 等 DTO
  └─ 已有 ChatMemoryLlmWithTimeoutAsync → 统一的 LLM 调用封装

SubconsciousWorkerService（PuddingRuntime）
  ├─ 已有 Channel<ConsolidationJob> 消费
  └─ 已有 BackgroundService 生命周期

IMemoryLibrary 关键方法
  ├─ ListBooksScopedAsync  → 列出所有 Book（含状态）
  ├─ ArchiveBookAsync      → 归档
  ├─ DeleteBookAsync       → 删除（已有但框架 bug 待修）
  ├─ DeleteChapterAsync    → 删除章节
  ├─ MergeBranchAsync      → 合并分支
  └─ FindBookByTitleAsync  → 按标题查找

IMemoryLibrarian 关键方法
  ├─ IngestExperienceAsync  → 结构化写入（Background Extractor 使用）
  └─ PlanTreeMaintenanceAsync → 树维护计划
```

---

## 二、目标行为

```
AutoDreamAsync(workspaceId)
  ├─ Phase 1: Scan ─── 收集记忆库全貌
  │   ├─ ListBooksScopedAsync → 所有 Book（active + archived）
  │   ├─ For each Book → ListChaptersAsync（含 UpdatedAt）
  │   └─ 构建 MemorySnapshot DTO（JSON，发给 Flash LLM）
  │
  ├─ Phase 2: Plan ─── Flash LLM 分析并制定清理计划
  │   ├─ 输入: MemorySnapshot + 清理规则提示词
  │   ├─ ChatMemoryLlmWithTimeoutAsync → AutoDreamPlan
  │   └─ 输出: Operations[] ← 每种操作不超过 5 条
  │
  ├─ Phase 3: Execute ─── 逐条执行清理
  │   ├─ MERGE: 同名 Book → 合并 Chapters → 归档源
  │   ├─ ARCHIVE: 标记过时 → ArchiveBookAsync
  │   ├─ DELETE: archived + >30 天无更新 → DeleteBookAsync
  │   └─ 每条操作记录日志
  │
  ├─ Phase 4: Rebuild ─── 重建索引（此处只生成报告，INDEX.md 由后续管道负责）
  │   └─ 生成 AutoDreamReport
  │
  └─ Phase 5: Report ─── 产出报告写入日志
      └─ [AutoDream] 操作数 | 合并 X | 归档 Y | 删除 Z | 耗时 Nms
```

---

## 三、改动文件

### 文件 1: `ISubconsciousOrchestrator.cs`（接口）

位置：`PuddingCore/Abstractions/ISubconsciousOrchestrator.cs`

新增方法签名：

```csharp
/// <summary>
/// 定期记忆整理：扫描 → 分析 → 去重合并 → 过期清理 → 报告
/// 由 SubconsciousWorkerService 定时触发
/// </summary>
Task<AutoDreamReport> AutoDreamAsync(
    string workspaceId,
    MemoryLlmConfig? memoryLlmConfig = null,
    CancellationToken ct = default);
```

### 文件 2: `SubconsciousDtos.cs`（DTO）

位置：`PuddingCore/Platform/SubconsciousDtos.cs`

新增 DTO：

```csharp
/// <summary>Auto-Dream 执行报告</summary>
public sealed record AutoDreamReport
{
    public long DurationMs { get; init; }
    public int Merged { get; init; }        // 合并了几组重复 Book
    public int Archived { get; init; }      // 归档了几个
    public int Deleted { get; init; }       // 删除了几个
    public int Suggested { get; init; }     // Flash LLM 建议总数
    public int Executed { get; init; }      // 实际执行数
    public string? Summary { get; init; }   // 人类可读摘要
    public DateTime Timestamp { get; init; }
}

/// <summary>给 Flash LLM 的快照</summary>
public sealed record MemorySnapshot
{
    public int TotalBooks { get; init; }
    public int ActiveBooks { get; init; }
    public int ArchivedBooks { get; init; }
    public int TotalChapters { get; init; }
    public MemorySnapshotBook[] Books { get; init; } = [];
}

public sealed record MemorySnapshotBook
{
    public string BookId { get; init; } = "";
    public string Title { get; init; } = "";
    public string Status { get; init; } = "";       // active | archived
    public string Summary { get; init; } = "";
    public int ChapterCount { get; init; }
    public DateTime? LastUpdated { get; init; }
    public string[] ChapterTitles { get; init; } = []; // 前 10 个章节标题
}
```

### 文件 3: `SubconsciousOrchestrator.cs`（实现）

位置：`PuddingMemoryEngine/Services/SubconsciousOrchestrator.cs`

新增方法 `AutoDreamAsync`（约 200 行）：

```csharp
public async Task<AutoDreamReport> AutoDreamAsync(
    string workspaceId,
    MemoryLlmConfig? memoryLlmConfig = null,
    CancellationToken ct = default)
{
    var sw = Stopwatch.StartNew();
    var report = new AutoDreamReport { Timestamp = DateTime.UtcNow };

    // ── Phase 1: Scan ──
    var snapshot = await BuildMemorySnapshotAsync(workspaceId, ct);
    _logger.LogInformation("[AutoDream] Phase1-Scan: {Total} books ({Active} active, {Archived} archived)",
        snapshot.TotalBooks, snapshot.ActiveBooks, snapshot.ArchivedBooks);

    if (snapshot.TotalBooks <= 10 && snapshot.ArchivedBooks == 0)
    {
        // 记忆库还很小，跳过
        _logger.LogInformation("[AutoDream] Skipped: memory library too small ({Total} books)", snapshot.TotalBooks);
        return report;
    }

    // ── Phase 2: Plan ──
    var config = memoryLlmConfig ?? MemoryLlmConfig.CreateFlash();
    var plan = await PlanAutoDreamAsync(snapshot, config, ct);

    if (plan is not { Length: > 0 })
    {
        _logger.LogInformation("[AutoDream] Phase2-Plan: no operations suggested");
        return report;
    }

    report.Suggested = plan.Length;
    _logger.LogInformation("[AutoDream] Phase2-Plan: {Count} operations suggested", plan.Length);

    // ── Phase 3: Execute ──
    int executed = 0, merged = 0, archived = 0, deleted = 0;

    foreach (var op in plan.Take(5)) // 每次最多执行 5 条
    {
        try
        {
            switch (op.Kind)
            {
                case "merge":
                    if (await ExecuteMergeAsync(op, workspaceId, ct))
                    { merged++; executed++; }
                    break;
                case "archive":
                    if (await ExecuteArchiveAsync(op, ct))
                    { archived++; executed++; }
                    break;
                case "delete":
                    if (await ExecuteDeleteAsync(op, ct))
                    { deleted++; executed++; }
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AutoDream] Failed operation: {Kind} {BookId}", op.Kind, op.BookId);
        }
    }

    report.Merged = merged;
    report.Archived = archived;
    report.Deleted = deleted;
    report.Executed = executed;
    report.DurationMs = sw.ElapsedMilliseconds;
    report.Summary = $"合并 {merged} 组, 归档 {archived}, 删除 {deleted}, 耗时 {report.DurationMs}ms";

    _logger.LogInformation("[AutoDream] Completed: {Summary}", report.Summary);
    return report;
}
```

#### Phase 1 辅助方法：BuildMemorySnapshotAsync

```csharp
private async Task<MemorySnapshot> BuildMemorySnapshotAsync(
    string workspaceId, CancellationToken ct)
{
    var books = await _memoryLibrary.ListBooksScopedAsync(workspaceId, limit: 100, ct);
    var snapshotBooks = new List<MemorySnapshotBook>();

    foreach (var book in books)
    {
        var chapters = await _memoryLibrary.ListChaptersAsync(book.BookId, ct);
        snapshotBooks.Add(new MemorySnapshotBook
        {
            BookId = book.BookId,
            Title = book.Title,
            Status = book.Status,
            Summary = book.Summary ?? "",
            ChapterCount = chapters.Count,
            LastUpdated = chapters.MaxBy(c => c.UpdatedAt)?.UpdatedAt,
            ChapterTitles = chapters.OrderByDescending(c => c.UpdatedAt)
                                    .Take(10)
                                    .Select(c => c.Title)
                                    .ToArray()
        });
    }

    return new MemorySnapshot
    {
        TotalBooks = books.Count,
        ActiveBooks = books.Count(b => b.Status == "active"),
        ArchivedBooks = books.Count(b => b.Status == "archived"),
        TotalChapters = snapshotBooks.Sum(b => b.ChapterCount),
        Books = snapshotBooks.ToArray()
    };
}
```

#### Phase 2 辅助方法：PlanAutoDreamAsync

```csharp
private async Task<AutoDreamOperation[]> PlanAutoDreamAsync(
    MemorySnapshot snapshot, MemoryLlmConfig config, CancellationToken ct)
{
    var systemPrompt = BuildAutoDreamSystemPrompt();
    var userPrompt = System.Text.Json.JsonSerializer.Serialize(snapshot,
        new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

    var rawResponse = await ChatMemoryLlmWithTimeoutAsync(
        systemPrompt, userPrompt, config,
        stage: "auto-dream.plan", round: null, ct: ct);

    if (string.IsNullOrWhiteSpace(rawResponse)) return [];

    var json = ExtractJson(rawResponse);
    if (json == null) return [];

    try
    {
        var plan = System.Text.Json.JsonSerializer.Deserialize<AutoDreamPlan>(json,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return plan?.Operations ?? [];
    }
    catch
    {
        _logger.LogWarning("[AutoDream] Failed to parse plan JSON");
        return [];
    }
}

private static string BuildAutoDreamSystemPrompt()
{
    return """
        你是 Pudding 的记忆整理服务。分析记忆库快照，提出清理计划。

        ### 清理规则（四步判断法）
        1. **不准确/过时** → 建议 archive（如 heartbeat_interval: 10min 已改为 24h）
        2. **不会再被用到** → 建议 archive（如终端测试垃圾 "echo terminal test"）
        3. **冗余重复** → 建议 merge（同名 Book 且 summary 相同 → 合并 chapters，然后 archive 源）
        4. **应保留** → 不操作（唯一有实际内容的 Book）

        ### 安全约束
        - archived + >30天未更新 → 可建议 delete
        - 决策记录（决策记录 Book）→ 永远不删，但可 archive 旧的
        - 用户档案/偏好 → 永远保留 active
        - 项目知识 → 永远保留 active
        - 每次最多建议 5 条操作

        ### 输出格式（JSON）
        {
          "operations": [
            {
              "kind": "merge|archive|delete",
              "reason": "一句话原因",
              "bookId": "目标 BookId",
              "sourceBookId": "合并时的源 BookId（仅 merge 需要）",
              "priority": 1-5  // 1=最优先
            }
          ]
        }
        """;
}

// DTO for parsing LLM output
private sealed record AutoDreamPlan { public AutoDreamOperation[] Operations { get; init; } = []; }
private sealed record AutoDreamOperation
{
    public string Kind { get; init; } = "";
    public string Reason { get; init; } = "";
    public string BookId { get; init; } = "";
    public string? SourceBookId { get; init; }
    public int Priority { get; init; }
}
```

#### Phase 3 辅助方法：ExecuteMergeAsync / ExecuteArchiveAsync / ExecuteDeleteAsync

```csharp
private async Task<bool> ExecuteMergeAsync(
    AutoDreamOperation op, string workspaceId, CancellationToken ct)
{
    if (string.IsNullOrEmpty(op.SourceBookId)) return false;

    var source = await _memoryLibrary.GetBookAsync(op.SourceBookId, ct);
    var target = await _memoryLibrary.GetBookAsync(op.BookId, ct);
    if (source == null || target == null) return false;

    // 把 source 的 chapters 移到 target
    var chapters = await _memoryLibrary.ListChaptersAsync(op.SourceBookId, ct);
    foreach (var ch in chapters)
    {
        await _memoryLibrary.AddChapterAsync(
            op.BookId, ch.Title, ch.Content,
            sourceSessionId: ch.SourceSessionId, ct: ct);
    }

    // 归档 source
    await _memoryLibrary.ArchiveBookAsync(op.SourceBookId, ct);

    _logger.LogInformation("[AutoDream] Merged {Source} → {Target} ({Count} chapters): {Reason}",
        source.Title, target.Title, chapters.Count, op.Reason);
    return true;
}

private async Task<bool> ExecuteArchiveAsync(
    AutoDreamOperation op, CancellationToken ct)
{
    var book = await _memoryLibrary.GetBookAsync(op.BookId, ct);
    if (book == null) return false;

    await _memoryLibrary.ArchiveBookAsync(op.BookId, ct);

    _logger.LogInformation("[AutoDream] Archived {Title}: {Reason}", book.Title, op.Reason);
    return true;
}

private async Task<bool> ExecuteDeleteAsync(
    AutoDreamOperation op, CancellationToken ct)
{
    var book = await _memoryLibrary.GetBookAsync(op.BookId, ct);
    if (book == null) return false;
    if (book.Status != "archived") return false; // 只删 archived

    // 额外安全检查：>30 天未更新
    var chapters = await _memoryLibrary.ListChaptersAsync(op.BookId, ct);
    var lastUpdate = chapters.MaxBy(c => c.UpdatedAt)?.UpdatedAt;
    if (lastUpdate.HasValue && (DateTime.UtcNow - lastUpdate.Value).TotalDays < 30)
        return false; // 不够 30 天，跳过

    await _memoryLibrary.DeleteBookAsync(op.BookId, ct);

    _logger.LogInformation("[AutoDream] Deleted {Title}: {Reason}", book.Title, op.Reason);
    return true;
}
```

### 文件 4: `SubconsciousWorkerService.cs`（定时触发）

位置：`PuddingRuntime/Services/Background/SubconsciousWorkerService.cs`

在 `ExecuteAsync` 中新增定时器：

```csharp
// 在现有 Channel 消费循环之外，增加定时触发
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    // ... 现有 Channel 消费逻辑保持不变 ...

    // ── Auto-Dream 定时触发 ──
    // 使用独立 Task 运行，不阻塞 Channel 消费
    _ = Task.Run(() => AutoDreamLoopAsync(stoppingToken), stoppingToken);
}

private async Task AutoDreamLoopAsync(CancellationToken ct)
{
    // 首次延迟 5 分钟（等系统稳定），之后每 6 小时检查
    await Task.Delay(TimeSpan.FromMinutes(5), ct);

    while (!ct.IsCancellationRequested)
    {
        try
        {
            var lastRun = await GetLastAutoDreamTimeAsync(ct);
            var hoursSinceLastRun = (DateTime.UtcNow - lastRun).TotalHours;

            // 触发条件：距上次 ≥ 12h
            if (hoursSinceLastRun >= 12)
            {
                _logger.LogInformation("[AutoDream] Triggering periodic maintenance (last run: {Hours}h ago)",
                    hoursSinceLastRun.ToString("F1"));

                await _orchestrator.AutoDreamAsync("default", null, ct);
                await SetLastAutoDreamTimeAsync(DateTime.UtcNow, ct);
            }
        }
        catch (OperationCanceledException) { break; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AutoDream] Timer loop error");
        }

        await Task.Delay(TimeSpan.FromHours(6), ct);
    }
}

// 简单的持久化：存到文件或 DB
private async Task<DateTime> GetLastAutoDreamTimeAsync(CancellationToken ct)
{
    // 读取 workspace 中 .pudding/last-autodream.txt
    // 返回 DateTime，无记录则返回 DateTime.MinValue
}

private async Task SetLastAutoDreamTimeAsync(DateTime time, CancellationToken ct)
{
    // 写入 workspace 中 .pudding/last-autodream.txt
}
```

---

## 四、架构约束

| # | 约束 | 原因 |
|---|------|------|
| 1 | ❌ 不新建服务 | 复用 SubconsciousOrchestrator + WorkerService |
| 2 | ❌ 不新建项目 | 只在现有 PuddingMemoryEngine/PuddingRuntime 中改 |
| 3 | ❌ 不阻塞主对话 | 全程后台异步，定时器独立 Task |
| 4 | ❌ 不使用 Pro LLM | 只用 Flash 模型（低成本） |
| 5 | ✅ 每次最多 5 条操作 | 渐进式，避免大爆炸 |
| 6 | ✅ 只删 archived + >30 天 | 防止误删 |
| 7 | ✅ 全部操作有日志 | `[AutoDream]` 标签，可 grep |
| 8 | ✅ 失败不中断 | 单条失败记录日志后继续 |

---

## 五、可观测

### 日志格式

```
[AutoDream] Phase1-Scan: 34 books (25 active, 9 archived)
[AutoDream] Phase2-Plan: 3 operations suggested
[AutoDream] Merged 交接索引 → 交接索引 (5 chapters): 重复Book
[AutoDream] Archived 终端执行: echo "terminal test": 无用垃圾
[AutoDream] Deleted 用户偏好(v3): archived >30天, 重复
[AutoDream] Completed: 合并 1 组, 归档 1, 删除 1, 耗时 4521ms
```

### Agent 自查

```
query_session_logs(grep="[AutoDream]")
agent_diagnostics(cache_health)
grep_memory(list_books)  → 看 Book 数变化
```

### 关键指标

| 指标 | 目标 | 测量 |
|------|------|------|
| 总 Book 数 | < 25 | `grep_memory(list_books)` |
| 重复 Book | 0 | `manage_memory(dedup_report)` |
| 每次操作数 | 1-5 | 日志 |
| 耗时 | < 10s | `DurationMs` |
| 触发频率 | 12h 间隔 | 日志时间戳 |

---

## 六、验收标准

### 功能
- [ ] 定时触发：距上次 ≥ 12h 后自动执行
- [ ] 重复 Book 被识别并合并（同名+同summary）
- [ ] 无用 Book 被归档/删除（终端测试垃圾等）
- [ ] 决策/用户档案/项目知识不受影响
- [ ] 每次最多执行 5 条操作

### 性能
- [ ] 单次 AutoDreamAsync < 10s
- [ ] 不阻塞主对话（独立后台 Task）

### 可观测
- [ ] 日志含 phases 信息（扫描/计划/执行/完成）
- [ ] 可通过 `query_session_logs(grep="[AutoDream]")` 查看
- [ ] AutoDreamReport 中有操作摘要

---

## 七、与现有管道的关系

```
Pre-Compaction Flush ─── 压缩前抢救事实 → [PreCompactFlush] 消息
         ↓
Background Extractor ─── 会话后搬运事实 → 记忆图书馆 Chapter
         ↓
Auto-Dream ──────────── 定期整理记忆图书馆 → 去重/合并/过期清理
         ↓
管道2: 经验→SKILL ──── 从整理后的记忆 + 会话日志 → 提炼 SKILL（后续规划）
```

---

## 八、实施检查清单

- [ ] 1. `SubconsciousDtos.cs` — 新增 AutoDreamReport, MemorySnapshot, MemorySnapshotBook DTO
- [ ] 2. `ISubconsciousOrchestrator.cs` — 新增 AutoDreamAsync 签名
- [ ] 3. `SubconsciousOrchestrator.cs` — 新增 AutoDreamAsync + 5 个辅助方法
- [ ] 4. `SubconsciousWorkerService.cs` — 新增 AutoDreamLoopAsync 定时器
- [ ] 5. `dotnet build` — 编译通过，0 errors
- [ ] 6. 重启验证 — 观察日志中 `[AutoDream]` 记录
