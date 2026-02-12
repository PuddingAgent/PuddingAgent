# 实施规约：Pre-Compaction Flush（借鉴 Claude Code）

> 版本：v1.0 | 日期：2026-07-11
> 借鉴来源：Claude Code 的 pre-compaction flush 模式
> 原则：最小改动、可观测、不破现有架构

---

## 一、要做什么

在上下文压缩（compaction）之前，给 Agent 一次快速保存关键信息的机会。

```
当前流程：
  窗口满 → CompactAsync → 信息丢失

目标流程：
  窗口满 → 💾 FlushSave → CompactAsync → 信息保留
```

## 二、架构原则

1. **最小改动** — 只改 `ContextWindowManager.TryAutoCompactAsync`，新增一个扩展点
2. **不重复发明** — 复用已有的 `save_memory`、`FlashContextCompactionSummaryGenerator` 路径
3. **异步不阻塞** — Flush 失败不影响压缩继续执行（降级优雅）
4. **可观测** — 每个关键节点打日志，Agent 可自查

## 三、改动文件清单

### 文件 1：`components/context_pipeline/ContextWindowManager.cs`

**位置**：`TryAutoCompactAsync` 方法内，`CompactAsync` 调用之前

**改动**：插入 Flush 步骤

```
// 现有代码（伪代码）
async Task TryAutoCompactAsync(...) {
    var health = await GetHealthAsync();
    if (!health.ShouldAutoCompact) return;
    
    // ++++++++++ 新增 START ++++++++++
    await FlushMemoriesBeforeCompactionAsync(session, cancellationToken);
    // ++++++++++ 新增 END ++++++++++
    
    await CompactAsync(session, cancellationToken);
}

// 新增方法
async Task FlushMemoriesBeforeCompactionAsync(Session session, CancellationToken ct) {
    var sw = Stopwatch.StartNew();
    try {
        // 1. 注入提示：告诉 Agent 即将压缩，选最重要的保存
        var flushPrompt = BuildFlushPrompt(session);
        
        // 2. 用 Flash 模型快速提取（不要用主模型，避免增加压缩耗时）
        var facts = await _flashGenerator.ExtractKeyFactsAsync(flushPrompt, ct);
        
        // 3. 写入记忆库
        foreach (var fact in facts) {
            await _memoryService.SaveAsync(fact, ct);
        }
        
        // 4. 日志（可观测）
        _logger.LogInformation(
            "[PreCompactFlush] saved={Count} duration={Duration}ms",
            facts.Count, sw.ElapsedMilliseconds);
    }
    catch (Exception ex) {
        // 5. 失败不影响主流程
        _logger.LogWarning("[PreCompactFlush] failed: {Error}", ex.Message);
    }
}

string BuildFlushPrompt(Session session) {
    return @"
[系统消息] 上下文窗口即将压缩。提取以下信息保存：
1. 用户偏好和沟通风格
2. 本次会话的关键决策
3. 新发现的项目事实
4. 重复出现的模式/教训

不要保存：
- 能用 git log 找到的东西
- 进行中的任务状态
- 一次性操作细节
- 调试过程
";
}
```

### 文件 2：`components/context_pipeline/FlashContextCompactionSummaryGenerator.cs`

**改动**：新增 `ExtractKeyFactsAsync` 方法

```
// 新增方法
async Task<List<MemoryFact>> ExtractKeyFactsAsync(string flushPrompt, CancellationToken ct) {
    var model = await ResolveFlashModel();
    
    // 定义输出结构
    var schema = new {
        facts = new[] {
            new { type = "user|project|feedback|reference", content = "" }
        }
    };
    
    // 调用 Flash LLM，限制 max_tokens 控制成本
    var result = await _llmClient.GenerateStructuredAsync(
        model, flushPrompt, schema, 
        maxTokens: 512,  // 控制成本
        cancellationToken: ct);
    
    return result.facts.Select(f => new MemoryFact {
        Type = f.type,
        Content = f.content,
        Source = "pre-compaction-flush"
    }).ToList();
}
```

### 文件 3：`components/context_pipeline/IMemoryService.cs`（新增接口）

```
// 轻量接口，不引入新依赖
public interface IMemoryFlushService {
    Task SaveAsync(MemoryFact fact, CancellationToken ct);
}

public record MemoryFact {
    public string Type { get; init; }     // user|project|feedback|reference
    public string Content { get; init; }
    public string Source { get; init; }
}
```

---

## 四、可观测设计

### 日志格式

```
[PreCompactFlush] started session={sid} tokens={before}
[PreCompactFlush] extracted facts={N} types={user:X,project:Y,...}
[PreCompactFlush] completed duration={ms}saved={N}
[PreCompactFlush] failed reason={error}  ← 降级不阻塞
```

### Agent 自查方式

```
query_session_logs(grep="[PreCompactFlush]")
→ 可查看：触发频率、每次提取数量、平均耗时
```

### 关键指标

| 指标 | 含义 | 目标 |
|------|------|------|
| flush 触发次数 | 压缩前冲洗频率 | 每次压缩触发 |
| facts 提取数 | 每次保存几条 | 1-5 条 |
| flush 耗时 | Flash LLM 调用耗时 | < 2s |
| flush 成功率 | 失败降级比例 | > 95% |

---

## 五、不会发生的（架构约束）

1. ❌ 不在压缩关键路径上阻塞 — Flush 失败 → 日志警告 → 继续压缩
2. ❌ 不使用主模型 — 只用 Flash，控制成本和延迟
3. ❌ 不修改 `AgentExecutionService` — 改动局限在 `ContextWindowManager`
4. ❌ 不创建新项目/新 NuGet 包 — 所有改动在现有 `PuddingRuntime` 内
5. ❌ 不改变现有 DI 注册 — 新接口通过构造函数注入，DI 已有基础设施

---

## 六、测试验收

### 单元测试
- `FlushMemoriesBeforeCompactionAsync` 提取到 0 条 → 正常返回，不抛异常
- Flash LLM 调用失败 → 捕获异常，日志警告，压缩继续
- 提取到 3 条 fact → 3 条都调用 `SaveAsync`

### 集成测试
- 触发 `/compact` → 检查日志有 `[PreCompactFlush]`
- 压缩后 `search_memory("pre-compaction-flush")` 可查到保存的内容

---

## 七、后续演进（本 PR 不做）

- [ ] 记忆分类细化（user/project/feedback/reference 四种类型）
- [ ] 去重检查（Flush 前先查已有记忆避免重复）
- [ ] 新鲜度衰减（>1 天的 flush 记忆标注过期）
- [ ] `session.closed` HOOK（Background Extractor，管道1）
- [ ] `cron.daily` HOOK（Auto-Dream 定期合并）
