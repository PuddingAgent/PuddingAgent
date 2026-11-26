---
title: 回执路径基准与 GetRelativePath 使用约定（2026-11-26）
author: hyfree
date: 2026-11-26
last_reviewed: 2026-11-26
status: active
description: "工具回执里的路径基准只有两种合法选择：调用方给的执行根（context.WorkingDirectory），或该次操作自己的根（搜索根/列举根/agent 根）。不得用缓存的静态 WorkspaceRoot —— 部署目录里它向上找不到仓库标记会回落到进程 cwd（…\\win-x64\\core），产出调用方无法定位的 '..\\..\\..\\..\\Data\\…'。越界判定必须按分隔符边界，因为 '..foo' 是合法文件名；相对形态一律由 HostFileToolPaths.RenderForReceipt 产出，file_write 是唯一例外（固定回执绝对路径，FileTools.cs:582）。本文给出全仓 16 处 GetRelativePath 的逐一分类、f013b49 的运行时两臂取证，以及 file_write/file_patch/list_dir 的界内/界外四臂实测矩阵。"
categories: [conventions, runtime]
tags: [paths, receipts, list_dir, file_write, relative-path, evidence]
related_docs: [Docs/14_reports/2026-11-26-exit-9009归因分支在当前构建不可达.md]
related_files: [Source/PuddingRuntime/Tools/BuiltIns/Files/FileTools.cs, Source/PuddingRuntime/Tools/BuiltIns/Search/SearchGrepTool.cs, Source/PuddingRuntime/Services/AgentExecution/ToolResultContextPolicy.cs]
slug: conventions-receipt-path-and-getrelativepath-2026-11-26
draft: false
---

# 回执路径基准与 GetRelativePath 使用约定

## 1. 判据（一条就够）

回执、报错、摘要里给调用方看的路径，只允许三种形态（前两项是“基准”，第三项是“没有基准”）：

| 基准 | 适用 | 例 |
|---|---|---|
| **执行根** = `context.WorkingDirectory` | 工具产出的“目标路径”字段 | `list_dir` 的 `Path`、`file_patch` 的 diff 头 |
| **绝对路径** | 需要无歧义定位的写入目标 | `file_write` 的 `Wrote …`（入参为相对时追加 `(resolved from '…')`） |
| **操作根** = 该次调用自己给的作用域 | 结果条目、命中行 | `search_grep` 的 `file:line`（相对搜索目录）、`list_dir` 的 `Entries[].Path`（相对列举根） |

三条都得满足同一件事：**调用方能直接拿它去下一次调用**。

⛔ 不得用缓存的静态 `WorkspaceRoot` 当基准。部署目录（`temp\build\winui3\bin\…\core`）里它向上 8 层找不到仓库标记，会回落到进程 cwd `…\win-x64\core`，与 `fullPath` 的解析基准不同，于是产出 `..\..\..\..\Data\…` 这种“只能自己数几个点”的路径（2026-11-26 运行时实测）。

## 2. 统一入口与越界判据

```csharp
HostFileToolPaths.RenderForReceipt(fullPath, context.WorkingDirectory)
// 界内 → 相对执行根；界外 / 跨盘（rooted）→ 原样返回绝对路径
```

越界判定必须按分隔符边界：

```csharp
relative.Length >= 2 && relative[0] == '.' && relative[1] == '.'
    && (relative.Length == 2 || relative[2] == '\\' || relative[2] == '/')
```

`StartsWith("..")` 会把合法文件名 `..foo` 误判成越界。跨盘时 `Path.GetRelativePath` 直接返回 rooted 路径，由 `Path.IsPathRooted(relative)` 兜住；`ArgumentException` 分支覆盖空路径与非法字符。

`file_write` 是唯一**不走** `RenderForReceipt` 的回执路径（`FileTools.cs:582` 直接拼 `fullPath`）：写入目标必须无歧义，「字节落在哪」不容许调用方自己再拼一次基准。它与 `file_patch` 的差异是**有意**的，不是漏改——见 §5 的实测矩阵。

## 3. 全仓 16 处逐一分类（2026-11-26 现场 grep）

| 文件:行 | 基准 | 是否合规 | 说明 |
|---|---|---|---|
| `FileTools.cs:77`（`RenderForReceipt` 本体） | 执行根 | ✅ 加越界退回 | 唯一需要改的一处，`f013b49` 已改 |
| `FileTools.cs:721` | 列举根 | ✅ 无需改 | `list_dir` 条目路径 |
| `SearchGrepTool.cs:856` | 搜索根 | ✅ | 索引侧相对路径 |
| `SearchGrepTool.cs:985`（`cwd = directory` @ :866） | 搜索根 | ✅ | 故命中行是 `Tools\…:12:`，不是 `..\..\..\…` |
| `SearchGrepTool.cs:1248` | `searchDir` | ✅ | 同上 |
| `IndexScopeResolver.cs:68` | 作用域本身 | ✅ | 内部包含性判定 |
| `FileSearchTool.cs:972` | `rootDirectory` | ✅ | 文件搜索结果 |
| `FileNoisePathFilter.cs:72` | `searchRoot` | ✅ | 噪声目录过滤，内部 |
| `MissingPathDiagnostics.cs:89` | 最深存在祖先 | ✅ | 产出「还差几级」而非路径 |
| `AgentSkillFileService.cs:373` | agent 私有根 | ✅ | 技能文件 |
| `ToolResultContextPolicy.cs:166` | `ResolveWorkspaceRoot(workingDirectory)` | ✅ | 溢出文件由 `EnsureDescendant` 保证在界内 |
| `ToolResultContextPolicy.cs:171` | 同上 | ✅ | manifest 指针 |

结论：**同一缺陷类只命中 `list_dir` 的 `Path` 一处**，其余要么按操作根，要么有 `EnsureDescendant` 之类的界内保证。下次不必重查这套。

## 4. 运行时两臂取证（`f013b49`，2026-11-26 16:07 部署）

```
commit f013b49 16:05:29 → live DLL 16:06:56–16:07:00 → PID 113424 @16:07:30
模块路径取自 Get-Process.Modules（temp\build\winui3\…\core\*.dll）
```

| 臂 | 入参 | 回执 `Path` |
|---|---|---|
| 界外 | `D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingRuntime\Tools\BuiltIns\Files` | **绝对路径**（界外退回生效） |
| 界内（正向对照） | `temp` | `temp`（相对行为未变） |

反事实（同一路径按旧实现算）：`..\..\..\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingRuntime\Tools\BuiltIns\Files`。

## 5. 边界（不声称的部分）

- **四臂已做运行时取证**（2026-11-26 16:31，live 构建 `70acf5c`）：

| 工具 | 界内 `D:\Data\workspaces\default\temp\…` | 界外 `D:\CodeProject\…\temp\…` |
|---|---|---|
| `file_write` | 绝对路径；相对入参时追加 `(resolved from '…')` | 绝对路径 |
| `file_patch` | 相对：`temp\…: patched (1 replacements)` | 绝对路径 |
| `list_dir` | 相对：`Path` = `temp` | 绝对路径（`f013b49`） |

  因此这条约定要读成「**要么执行根相对、要么绝对路径**」，不能读成「三者统一走 `RenderForReceipt`」。两种形态都满足「调用方能直接拿去下一次调用」；是否统一（把 `file_write` 也改成相对）牵动回执契约，**未定，本文不预判**。
- 探针文件三个已删并复核 `Test-Path=False`；界外调用成立的前提是 YOLO 模式。
- 本次界外调用能成立，前提是当前处于 YOLO 模式；非 YOLO 下界外路径会在 `TryResolveInsideWorkspace` 被拒，回执路径问题不出现。
