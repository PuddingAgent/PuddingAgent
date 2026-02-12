# Pudding Agent 项目指令

## 项目概述
Pudding 是一个 .NET 10 的自主 Agent 框架，支持六层记忆体系、Skill 系统、子代理委派、潜意识后台管道。

## 项目路径
- 主项目: `D:\data\Pudding`
- 工作空间: `D:\data\workspaces\default`
- 编译入口: `dotnet build PuddingRuntime`

## 运行时配置
- Shell: `pwsh` (PowerShell Core)
- OS: Windows 10
- 工作目录: `D:\data\Pudding`

## 代码修改约定
- 所有修改先 dry_run 预览 → 确认后 dry_run=false
- 编译命令: `dotnet build PuddingRuntime --no-restore`
- 不可绕过审批系统，需审批的工具走 request_tool_approval

## 关键子项目
| 项目 | 路径 | 职责 |
|------|------|------|
| PuddingCore | `D:\data\Pudding\PuddingCore` | 核心类型、DTO、接口 |
| PuddingMemoryEngine | `D:\data\Pudding\PuddingMemoryEngine` | 记忆引擎、潜意识管道 |
| PuddingRuntime | `D:\data\Pudding\PuddingRuntime` | 运行时、后台服务 |

## 关键文件
| 文件 | 路径 | 职责 |
|------|------|------|
| Program.cs | `PuddingRuntime/Program.cs` | 启动配置、DI、HttpClient |
| DirectLlmClient.cs | `PuddingRuntime/...` | LLM 客户端、流式超时 |
| FileTools.cs | `PuddingRuntime/...` | 文件编辑引擎（5层策略链） |
| SubconsciousOrchestrator.cs | `PuddingMemoryEngine/...` | 潜意识管道核心 |
| SubconsciousWorkerService.cs | `PuddingRuntime/...` | 后台定时器 |
| SubconsciousDtos.cs | `PuddingCore/...` | 管道 DTO |
| ISubconsciousOrchestrator.cs | `PuddingCore/...` | 管道接口 |

## 长效学习管道（已建成）
1. Pre-Compaction Flush — 压缩前抢救事实
2. Background Extractor — 会话后搬运事实
3. Auto-Dream — 定期整理（每6h）
4. 管道2：经验→SKILL — 黄金路径→技能（每12h）
5. Skill Self-Improvement — 技能自进化（每4h）

## 已知问题
- PuddingAgent.dll 编译后被运行中进程锁定，需重启 Pudding 部署
- SubconsciousWorkerService 新版代码需重启后生效
- 记忆库有 11 个 archived Books 待 Auto-Dream 清理
