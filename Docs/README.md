# PuddingCode 文档索引

> **项目定位**: 面向大众用户的智能助手，编程能力（PuddingCode）作为 Pro Mode 插件保留  
> **技术栈**: C# / .NET 10 · Spectre.Console · SemanticKernel  
> **仓库**: [github.com/hyfree/PuddingCode](https://github.com/hyfree/PuddingCode)

---

## 📚 文档导航

### 核心文档（入口点）

| 文档 | 说明 |
|------|------|
| **[Map.md](Map.md)** | 🏗 架构总览 - 系统架构、双态交互、Swarm 编排、视觉设计语言 |
| **[Tasks.md](Tasks.md)** | 📋 任务索引 - 所有设计与开发任务的状态跟踪、依赖关系、里程碑 |

### 设计文档（Tasks/）

详细设计方案，按任务编号排序：

| 编号 | 文档 | 状态 | 优先级 |
|------|------|------|--------|
| Task 18 | [产品定位与设计](Tasks/task18-positioning.md) | ✅ 已完成 | 🔴 P0 |
| Task 03 | [V0.1 实现方案](Tasks/task03-v01-plan.md) | ✅ 已完成 | 🔴 P0 |
| Task 04 | [蜂群模式设计](Tasks/task04-swarm.md) | ✏️ 设计中 | 🔴 P0 |
| Task 01 | [CLI 交互界面设计](Tasks/task01-interaction.md) | ✏️ 设计中 | 🟠 P1 |
| Task 09 | [Agent 生命周期](Tasks/task09-agent-lifecycle.md) | ✏️ 设计中 | 🔴 P0 |
| Task 10 | [Agent 能力体系](Tasks/task10-agent-capability.md) | ✏️ 设计中 | 🔴 P0 |
| Task 11 | [权限与安全沙盒](Tasks/task11-permission.md) | ✏️ 设计中 | 🔴 P0 |
| Task 12 | [感官过滤](Tasks/task12-sensory-filter.md) | ✏️ 设计中 | 🔴 P0 |
| Task 06 | [Agent 消息系统](Tasks/task06-agent-message.md) | ✏️ 设计中 | 🟠 P1 |
| Task 08 | [记忆系统](Tasks/task08-memory.md) | ✏️ 设计中 | 🟠 P1 |
| Task 05 | [Swarm 视图设计](Tasks/task05-swarm-view.md) | ✏️ 设计中 | 🟡 P2 |
| Task 07 | [Agent 命名系统](Tasks/task07-agent-naming.md) | ✏️ 设计中 | 🟡 P2 |
| Task 13 | [上下文预热](Tasks/task13-context-warmup.md) | ✏️ 设计中 | 🟡 P2 |
| Task 14 | [SKILL 插件化](Tasks/task14-skill-plugin.md) | ✏️ 设计中 | 🟡 P2 |
| Task 16 | [服务商与模型管理](Tasks/task16-provider-model.md) | ✏️ 设计中 | 🟡 P2 |
| Task 15 | [MCP 服务器集成](Tasks/task15-mcp-server.md) | ✏️ 设计中 | 🟢 P3 |
| Task 17 | [Leader 动态路由](Tasks/task17-leader-routing.md) | ✏️ 设计中 | 🟠 P1 |


### 功能规格（Features/）

| 文档 | 说明 |
|------|------|
| [LoginFeature.md](Features/LoginFeature.md) | 登录功能规格说明（用户名密码认证 + 桌面精灵集成） |

### 历史文档（Archive/）

| 文档 | 说明 |
|------|------|
| [讨论.md](Archive/讨论.md) | 项目早期设计讨论 - 行业瓶颈分析 + 6 层架构 + 5 个突破方向（历史参考） |

---

## 🗺 快速开始

### 第一次了解项目？

1. 从 [Map.md](Map.md) 开始，了解整体架构和设计哲学
2. 阅读 [task18-positioning.md](Tasks/task18-positioning.md)，理解产品定位
3. 查看 [Tasks.md](Tasks.md)，了解当前开发进度和待办任务

### 贡献代码前？

1. 确认任务状态：查看 [Tasks.md](Tasks.md) 中的依赖拓扑图
2. 阅读相关设计文档（`Tasks/` 目录下对应任务）
3. 遵循设计文档中的架构约束和技术规范

### 查找特定内容？

| 主题 | 文档位置 |
|------|----------|
| 系统架构 | [Map.md](Map.md) §一 |
| 视觉设计 | [Map.md](Map.md) §三 |
| 任务状态 | [Tasks.md](Tasks.md) §任务列表 |
| 开发路线图 | [Tasks.md](Tasks.md) §实现顺序 |
| 里程碑 | [Tasks.md](Tasks.md) §里程碑 |

---

## 📝 文档规范

### 命名约定

- **任务文档**: `taskNN-short-english-name.md` (e.g., `task04-swarm.md`)
- **核心文档**: 大写开头 (e.g., `Map.md`, `Tasks.md`)
- **目录**: 全小写 + 连字符 (e.g., `Features/`, `Archive/`)

### 链接规范

- 使用相对路径：`[文档名](Tasks/task04-swarm.md)`
- 目录名保持大小写一致：`Tasks/` 不是 `tasks/`

---

## 📞 联系与反馈

- **问题反馈**: [GitHub Issues](https://github.com/hyfree/PuddingCode/issues)
- **讨论**: [GitHub Discussions](https://github.com/hyfree/PuddingCode/discussions)

---

*最后更新：2026-02-20*
