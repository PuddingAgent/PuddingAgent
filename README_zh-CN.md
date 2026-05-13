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
docker run -p 8080:8080 pudding-agent
```

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

Pudding 的设计深受以下项目和研究的启发：

- **[Claude Code](https://github.com/anthropics/claude-code)** (Anthropic) — 工具接口设计、权限管线、Coordinator/Worker 模式
- **[Hermes-Agent](https://github.com/NousResearch/hermes-agent)** (Nous Research) — 自注册插件架构、记忆提供者模式、多平台统一路由
- **[OpenCode](https://github.com/anthropics/opencode)** — 结构化代码理解的瓶颈分析
- **[Cursor](https://cursor.com/)** — AI 编码编辑器的产品体验
- **[OpenHarness](https://github.com/anthropics/openharness)** — Harness 循环、Hook 系统、5 级安全边界
- **[OpenClaw](https://github.com/anthropics/openclaw)** — 记忆系统、多渠道 Gateway 架构
- **[OpenHanako](https://github.com/liliMozi/openhanako)** — 多层记忆编译管道(today/week/longterm/facts)、沙箱安全、插件热插拔
- **[SuperPowers](https://github.com/anthropics/superpowers)** — 技能系统、TDD 铁律、子代理驱动开发
- **[EVO MAP](https://github.com/nousresearch/evo-map)** — 经验胶囊概念、可移植知识打包
- **[Strange Loop Canon](https://www.strangeloopcanon.com/)** (Rohit Krishnan 等) — 多 Agent 协作机制、信息漂移、共享 Board 研究
- **[Strange Loop Canon 文章归档](https://www.strangeloopcanon.com/archive)** — Agent 经济实验的完整归档
- **[Building Effective Agents](https://www.anthropic.com/engineering/building-effective-agents)** (Anthropic) — Agent 设计模式指南
- **[AutoGPT](https://github.com/Significant-Gravitas/AutoGPT)** / **[BabyAGI](https://github.com/yoheinakajima/babyagi)** — 早期自主 Agent 的先驱探索
- **[LangChain](https://github.com/langchain-ai/langchain)** / **[CrewAI](https://github.com/crewAIInc/crewAI)** — Agent 框架的工程实践
- **[DeepSeek-TUI](https://github.com/Hmbown/deepseek-tui)** — 优雅的终端用户交互界面设计参考
- **[DeepSeek-TUI 官网](https://deepseek-tui.com/zh)** — 终端交互体验的实践案例

以及所有在 AI Agent 领域探索的开源贡献者和研究者。

---

## License

Apache License 2.0

---

<p align="center">
  <sub>「……交给我吧。」</sub>
</p>