# Pudding Agent

<p align="center">
  <img src="me.png" alt="Pudding" width="200"/>
</p>

<p align="center">
  <strong>你好，我是布丁。你的 AI 代理。</strong><br/>
  <sub>Hi. I'm Pudding. Your AI agent.</sub>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/version-v0.1.0-blue" alt="Version"/>
  <img src="https://img.shields.io/badge/platform-windows%20%7C%20linux%20%7C%20macos-lightgrey" alt="Platform"/>
  <img src="https://img.shields.io/badge/license-Apache%202.0-green" alt="License"/>
</p>

<p align="center">
  <strong>一个自包含、支持 P2P 组网的 AI 代理。下载、双击、开始对话。</strong><br/>
  <sub><a href="README.md">English README</a></sub>
</p>

---

## 这是什么？

Pudding 不是一个平台，不是一个框架，也不是一套微服务。

**Pudding 是一个运行在你电脑上的 AI 代理。**

一个文件。双击启动。浏览器自动打开。你说话，她干活。

她有自己的记忆（SQLite），自己的工具，自己的网页界面。不需要 PostgreSQL，不需要 Redis，不需要 RabbitMQ。

当你在局域网中运行多个 Pudding 时，她们会自动发现彼此——点对点，无需中心服务器。像蚂蚁一样协作：每只做自己看到的事，留下痕迹，其他蚂蚁接力完成。

<p align="center">
  <em>图书馆角落那个安静的女孩。看书，思考，等你吩咐。<br/>
  话不多。但事情总能办好。</em>
</p>

---

## 为什么叫 Pudding？

市面上的 AI 工具，要么是云端服务（你的数据不属于你），要么需要先起十几个 Docker 容器才能说"你好"。

Pudding 不一样：

- **她是你的。** 跑在你的机器上，数据留在你身边。
- **她很简单。** 一个文件，没有数据库安装，没有基础设施。
- **她有面孔。** 有名字，有形象，有记忆。她是你的代理，不是一个冷冰冰的 API。
- **她可以横向生长。** 需要更多代理时，再启动几个，自动组网。无需编排。

---

## 名字与形象

她叫 **布丁（Pudding）**。安静、高效、有一点神秘。

你给她一个任务。她微微歪头。几秒后。"好了。"

她有自己的笔记本（SQLite）。她记得你。她不会把你的秘密上传到云端。她只是在你的桌面、你的服务器、或者角落的树莓派上安静地工作。

<p align="center">
  <em>形象设计参考了某个图书馆文艺少女的气质——安静地阅读、理解、行动。<br/>
  没有多余的话，只有结果。</em>
</p>

---

## 快速开始

```bash
# 下载对应平台的可执行文件
# Windows: PuddingAgent.exe
# Linux:   PuddingAgent
# macOS:   PuddingAgent

# 运行
./PuddingAgent

# 浏览器会自动打开 -> http://localhost:8080
# 就这样。开始对话吧。
```

### Docker (可选)

```bash
# 端口映射：5000（主机）→ 8080（容器）
docker run -p 5000:8080 pudding-agent
# 浏览器访问 http://localhost:5000
```

---

## 开发/构建/发布

Pudding 提供了三条构建链路，适用于不同的使用场景：

### 日常开发（源码 watch，热更新，不依赖 Docker）

```powershell
.\dev-up.ps1
```

- 后端 `dotnet watch`：修改 `.cs` 文件后自动重启
- 前端 `pnpm run start:dev`：修改 `.tsx`/`.css` 后 HMR 热更新
- 前端开发服务器：http://localhost:8000
- 后端 API：http://localhost:5000
- 反向代理：本机访问 http://localhost/，局域网设备访问 `http://<宿主机局域网IP>/`
- 查看状态：`.\dev-up.ps1 -Status`
- 查看日志：`.\dev-up.ps1 -Logs`
- 停止：`.\dev-up.ps1 -Down`
- 重启：`.\dev-up.ps1 -Restart`
- 重新编译并启动：`.\dev-up.ps1 -Rebuild`
- 重启并重新编译：`.\dev-up.ps1 -Restart -Rebuild`
- 跳过依赖安装：`.\dev-up.ps1 -NoInstall`

### 本地诊断

机器可读的诊断日志与普通文本日志分离：

- 后端会话时间线：`data/logs/diagnostics/session-timeline/YYYYMMDD/{sessionId}.jsonl`
- 反向代理 SSE/replay 时间线：`data/logs/diagnostics/proxy/YYYYMMDD.jsonl`
- SQLite 事件表：`data/pudding_platform.db`
- 可聚合遥测事实：`data/pudding_platform.db` 中的 `telemetry_metric_events`
- 上下文分层指标：`data/pudding_platform.db` 中的 `context_layer_metric_events`

可观测是 Pudding/Hermes 的基础架构能力，不只是排障日志。新增运行时、工具、审批、记忆、子代理、基准测试或前端关键流程时，都应同时产出可还原证据和结构化指标。

可观测模型分三层：

- **Trace 原始证据**：JSONL 时间线、审批审计事件、会话日志、反代诊断和导出的诊断包，用于回答某一次运行为什么失败或变慢。
- **Metrics 结构化事实**：工具耗时、输出长度、审批路径、隐式审批耗时、上下文层 token 占比、层缓存命中率、记忆召回次数、benchmark 质量等可量化记录，优先写入 `telemetry_metric_events` 和专用指标表。
- **Insights 派生分析**：面向前端和脚本的聚合结论，例如隐式审批覆盖率、工具输出过长率、重试率、失败恢复率、benchmark 分数趋势和子代理贡献率。

处理流程分三阶段：

1. **数据采集**：在业务事件发生点记录稳定、可分组、可过滤的结构化字段，而不是只写自然语言日志。
2. **数据清洗和处理**：失败分类、脱敏、截断、hash、阈值判断、派生指标计算。
3. **数据展示**：通过管理后台诊断 UI、`Tools/Diagnostics` 和 `TestScripts` 脚本提供可查询、可复现的诊断结果。

独立 Python 工具位于 `Tools/Diagnostics`，使用仓库 `.venv` 执行：

```powershell
.\.venv\Scripts\python.exe Tools\Diagnostics\query_timeline.py --session-id <sessionId>
.\.venv\Scripts\python.exe Tools\Diagnostics\query_metrics.py telemetry-summary --days 7
.\.venv\Scripts\python.exe Tools\Diagnostics\query_metrics.py tool-usage --session-id <sessionId>
.\.venv\Scripts\python.exe Tools\Diagnostics\query_metrics.py tool-output --days 7 --min-chars 8192
.\.venv\Scripts\python.exe Tools\Diagnostics\query_metrics.py context-layers --days 30
.\.venv\Scripts\python.exe Tools\Diagnostics\inspect_schema.py
.\.venv\Scripts\python.exe Tools\Diagnostics\export_session_bundle.py --session-id <sessionId>
```

`/api/stats/tokens/context-layers` 和 `query_metrics.py context-layers` 用于上下文缓存优化分析，输出每层 token 占比、平均/中位数估算缓存命中率、层易变性、变化原因和 P95 token 大小。

开发环境默认开启诊断时间线。可通过 `PUDDING_DIAGNOSTICS_TIMELINE=0` 或 `PUDDING_DIAGNOSTICS_TIMELINE=1` 覆盖。
高细节遥测预览默认关闭。仅在本地排查时设置统一开关 `PUDDING_DEBUG=1`（兼容 `PUDDING_TELEMETRY_DEBUG=1`），系统会把脱敏/截断后的上下文与工具预览写入遥测 `debug_json`。`dev-up.py` 会在本地开发环境默认注入这两个开关。

### 上下文缓存与输入压缩

Pudding 的 token 成本治理分两层：

- **缓存命中观测**：通过 `prompt_cache_hit_tokens` / `prompt_cache_miss_tokens`、`telemetry_metric_events` 和 `context_layer_metric_events` 追踪每轮 LLM 调用的缓存命中率、上下文层变化和费用口径。
- **LLM 前置输入压缩**：设计中会在工具输出、日志、文件/search/diff 和 RAG 块进入 LLM 之前做局部压缩，并保留本地 artifact 取回能力；用户消息、system prompt 语义、最近轮次和当前执行轮默认保护。

我们研究了 [Headroom](https://github.com/chopratejas/headroom) 的做法：它通过内容类型路由、前缀稳定和 CCR（Compress-Cache-Retrieve）可逆取回来减少输入 token，并提升服务商前缀缓存命中机会。Pudding 会吸收这些设计思路，但默认优先建设原生的 `ContextInputCompression` 管线，使压缩、权限、SQLite、诊断包和缓存统计保持在同一治理边界内；Headroom 可作为开发期 benchmark、可选 proxy/provider 或实现参考。

相关设计文档：

- [ADR-042 上下文自动压缩与主动 Compact 命令](Docs/07架构/43ADR-042上下文自动压缩与主动Compact命令ADR.md)
- [上下文自动压缩与 Compact 命令设计方案](Docs/Features/上下文自动压缩与Compact命令设计方案.md)
- [ADR-018 上下文缓存可观测性体系](Docs/07架构/18上下文缓存可观测性ADR.md)
- [ADR-043 DeepSeek 上下文硬盘缓存统计闭环](Docs/07架构/44ADR-043缓存统计闭环ADR.md)

### 快速集成（验证本地 publish 产物与容器运行时组合）

```powershell
.\build-and-up.ps1 -Fast
```

可选跳过参数：

```powershell
.\build-and-up.ps1 -Fast -SkipFrontend    # 跳过前端构建
.\build-and-up.ps1 -Fast -SkipBackend     # 跳过后端 publish
.\build-and-up.ps1 -Fast -NoInstall       # 跳过 pnpm install
```

### 发布验证（生产构建 + 镜像）

```powershell
.\build-and-up.ps1
```

执行前端生产构建 → Docker 内 `dotnet publish` → 生成最终镜像。

---

## 技术选型

她的构建遵循一条规则：**用户零外部依赖。**

| 组件       | 技术                              |
| :--------- | :-------------------------------- |
| 运行时     | .NET (ASP.NET Core, 单文件发布)   |
| 数据库     | SQLite — 单文件，自动创建         |
| 前端       | React, 打包嵌入后端               |
| LLM        | 直接调用 API（OpenAI 兼容）       |
| P2P 网络   | mDNS 发现 + HTTP/gRPC 直连        |
| 记忆       | 本地持久化，私密安全              |

---

## 架构

```
┌──────────────────────────────────────┐
│         Pudding Agent（单进程）        │
│                                       │
│  浏览器 → localhost:8080              │
│  ┌─────────────────────────────────┐ │
│  │        Web UI（React）          │ │
│  ├─────────────────────────────────┤ │
│  │     Controller（路由/鉴权）      │ │
│  ├─────────────────────────────────┤ │
│  │     Runtime（LLM/工具/记忆）     │ │
│  ├─────────────────────────────────┤ │
│  │     P2P 网络层                  │ │
│  ├─────────────────────────────────┤ │
│  │     SQLite                      │ │
│  └─────────────────────────────────┘ │
│                                       │
│  ← P2P → 其他 Pudding Agent           │
└──────────────────────────────────────┘
```

完整架构：[Docs/架构.md](Docs/架构.md)

---

## 多 Agent 协作：蚁群模型

当你的网络中运行了多个 Pudding Agent：

1. 她们自动发现彼此（mDNS）
2. 直连通信（无需中心消息队列）
3. 事件广播（任务完成、状态变更）
4. 像蚂蚁一样分工协作

这不是编排。这是涌现。

---

## AI 时代与开源

AI 的来临彻底改变了开源软件的游戏规则。过去，Linux、ffmpeg 这样的伟大项目由卓越的程序员引导；如今，AI 正在逐渐承担起过去需要多年经验才能完成的工作，越来越多的普通人得以参与到开源事业中。

在 AI 时代，启动一个开源项目的成本在持续降低——但随之而来的可能也是开源的劣化。我们很难简单评价这件事，这或许是时代的阵痛。

但好的一面是：现在只要脑中有一个想法，AI 就能轻易为我们实现，而不再像从前那样，需要一行一行地花大量时间和精力去编写。**想法到现实的距离，越来越短。**

更有意思的是，AI 彻底改变了开源的参与方式。我们可以拥有自己的专属定制软件，派遣 Agent 为我们自己开发软件，而不是苦苦等待 OpenClaw 或 Hermes-Agent 去更新他们的版本。当我们需要某个功能时，可以直接 Fork 然后让 AI 实现它，而不是等待原作者哪天有空——也许，以后的开源项目，是由很多很多的 AI 参与的，而不仅仅是人类。

说到这个，**[EVO MAP](https://github.com/nousresearch/evo-map)** 这个项目很有意思——它提出的 **"经验胶囊"（Experience Capsule）** 概念是一个很酷的想法：就像哆啦 A 梦的"记忆面包"一样，将学到的知识打包成可移植、可复用的胶囊，供其他 Agent 消费。这正是激发 Pudding 记忆系统设计的灵感之一。

这个项目诞生于我对各种 AI 工具开发的探索。在思路上，它参考了许多优秀项目和产品的想法——不是抄袭，而是站在先行者的肩膀上，思考"下一代 AI 代理应该是什么样的"。

---

## 感谢

详见 [thanks.md](thanks.md)。

---

## License

Apache License 2.0

---

<p align="center">
  <sub>「……交给我吧。」</sub>
</p>
