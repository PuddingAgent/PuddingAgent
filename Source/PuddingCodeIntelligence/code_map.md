# PuddingCodeIntelligence CodeMAP

> 代码索引与分析 | LSP · 符号搜索 · 调用图 · 多语言

## 核心服务（Services/）

| 文件 | 用途 |
|------|------|
| `CodeIndexScheduler.cs` | 索引调度器 |
| `CodeIndexScopeRegistry.cs` | 索引范围注册 |
| `CodeIndexScopeResolver.cs` | 范围解析器 |
| `CodeProjectRegistry.cs` | 项目注册 |
| `CodeQueryService.cs` | 代码查询服务 |
| `DefaultCodeWorkspaceResolver.cs` | 工作区解析 |
| `DefaultProjectRootDetector.cs` | 项目根检测 |
| `CodePathIdentity.cs` | 路径标识 |
| `FileOutlinerRegistry.cs` | 文件大纲注册 |
| `IndexExcludePatterns.cs` | 排除模式 |

## 语言支持

| 目录 | 语言 |
|------|------|
| `CSharp/` | C# |
| `TypeScript/` | TypeScript |
| `Python/` | Python |
| `Cpp/` | C++ |
| `Json/` | JSON |
| `Yaml/` | YAML |
| `Markdown/` | Markdown |
| `PowerShell/` | PowerShell |
| `Bicep/` | Bicep |
| `Lsp/` | LSP 客户端 |

## 存储 & 契约

| 目录 | 用途 |
|------|------|
| `Storage/` | 索引存储 |
| `Contracts/` | 契约定义 |

## 测试

`../Tests/PuddingCodeIntelligenceTests/` — 代码索引测试
