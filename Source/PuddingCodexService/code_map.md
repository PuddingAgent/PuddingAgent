# PuddingCodexService CodeMAP

> Codex MCP Sidecar | 宿主外持久任务执行器

## 入口

| 文件 | 用途 |
|------|------|
| `Program.cs` | 独立进程入口 |

## 服务（Services/）

| 文件 | 用途 |
|------|------|
| `CodexTaskCoordinator.cs` | 任务协调器（核心，11KB） |
| `CodexMcpExecutor.cs` | MCP 执行器 |
| `FileCodexTaskStore.cs` | 文件任务持久化 |
| `ICodexExecutor.cs` | 执行器接口 |
| `SupervisorRestartRequestWriter.cs` | 重启请求写入 |

## 工具（Tools/）

| 文件 | 用途 |
|------|------|
| `CodexTaskTools.cs` | Codex 任务工具（5KB） |

## 配置 & 模型

| 文件/目录 | 用途 |
|------|------|
| `CodexServiceOptions.cs` | 服务选项（5KB） |
| `Models/` | 数据模型 |

## 测试

`../Tests/PuddingCodexServiceTests/` — MCP Service 测试
