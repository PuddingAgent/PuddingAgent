# Code Map V0.1 → V0.2 施工任务

## 当前底座（已就绪）

| 能力 | 工具 | 状态 |
|------|------|------|
| 文件结构预览 | `code_outline` | ✅ Roslyn 语法分析，毫秒级 |
| 符号搜索 | `code_symbol_search` | ✅ 索引已完成 |
| 符号展开 | `code_explore` | ✅ 依赖语义索引 |
| 调用者查询 | `code_callers` | ✅ 依赖语义索引 |
| 被调用者查询 | `code_callees` | ✅ 依赖语义索引 |
| 影响分析 | `code_impact` | ✅ 递归链路 |
| 全文搜索 | `search_grep` | ✅ Lucene 预构建 |

---

## Step 1: `code_outline` 友好提示

**文件：** `Source/PuddingRuntime/Tools/BuiltIns/CodeIntelligence/CodeOutlineTool.cs`

**改动：** `ExecuteCoreAsync` 开头加入守卫逻辑

```csharp
// 1. 文件存在性检查
if (!File.Exists(fullPath))
    return Fail($"(文件不存在: {filePath})");

// 2. 扩展名白名单（当前仅支持 .cs）
var ext = Path.GetExtension(fullPath).ToLowerInvariant();
if (ext != ".cs")
    return Fail($"(当前仅支持 C# 文件 (.cs)，不支持的扩展名: {ext})\n多语言支持计划在 V2.0 实现。");

// 3. 读取文件（捕获 IO 异常）
string source;
try { source = await File.ReadAllTextAsync(fullPath, ct); }
catch (Exception ex)
    return Fail($"(无法读取文件: {ex.Message})");

// 4. 语法分析 + 严重错误检测
var tree = CSharpSyntaxTree.ParseText(source);
var root = await tree.GetRootAsync(ct);
var errors = root.GetDiagnostics()
    .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
if (errors.Count > 0 && root.DescendantNodes().Count() < 5)
    return Fail($"(文件包含严重语法错误，无法生成结构预览)\n诊断:\n{string.Join("\n", errors.Take(5))}");

// 5. 遍历 + 空结果检查
var visitor = new OutlineSyntaxVisitor();
visitor.Visit(root);
if (visitor.RootNodes.Count == 0)
    return Fail("(文件中没有发现 C# 类型声明)");

// 6. 正常输出
return Ok(FormatTree(visitor.RootNodes));
```

**风险：** 🟢 低 — 已有文件加守卫，不改现有逻辑

---

## Step 2: `code_summary` 符号摘要工具

**新建文件：** `Source/PuddingRuntime/Tools/BuiltIns/CodeIntelligence/CodeSummaryTool.cs`

**功能：** 给定符号名（类/方法/属性），返回位置、文档注释、签名、关键调用者

**依赖：**
- `ICodeSymbolSearchService` — 定位符号
- `OutlineSyntaxVisitor` — 提取文档注释
- `code_callers/callees` 逻辑 — 获取调用关系

**输出示例：**

```
符号: IdleDetector.RecordActivity
类型: 方法
位置: PuddingRuntime/IdleDetector.cs:62
签名: public void RecordActivity()
文档: 记录当前时间戳为最近活跃时间，用于空闲检测

调用者 (2):
  1. RecordUserMessage()   @ IdleDetector.cs:45
  2. RecordToolCompleted() @ IdleDetector.cs:52

被调用者 (0):
  (无内部方法调用)
```

**风险：** 🟡 中等 — 需理解 `OutlineSyntaxVisitor` 和符号定位机制

---

## Step 3: `project_map` 项目模块概览

**新建文件：** `Source/PuddingRuntime/Tools/BuiltIns/CodeIntelligence/ProjectMapTool.cs`

**功能：** 给定项目根目录，返回命名空间 → 目录 → 核心类的映射

**输出示例：**

```
PuddingRuntime/
├── Services/
│   ├── Background/
│   │   ├── AgentHeartbeatService.cs   心跳后台服务
│   │   └── HeartbeatOrchestrator.cs   心跳协调器
│   ├── AgentWakeQueue.cs              多 Agent 唤醒队列
│   └── WakeRequest.cs                 唤醒请求模型
├── Tools/BuiltIns/CodeIntelligence/
│   ├── CodeOutlineTool.cs             文件结构预览
│   └── CodeSummaryTool.cs             符号摘要
├── IdleDetector.cs                    空闲检测器
└── ... (共 15 个文件)
```

**风险：** 🟡 中等 — 目录遍历 + 符号查询

---

## 执行建议

**顺序：** Step 1 → Step 2 → Step 3

每个步骤完成后，告诉我一声，我来验收。
