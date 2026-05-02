# Pudding Agent

<p align="center">
  <img src="me.png" alt="Pudding" width="200"/>
</p>

<p align="center">
  <strong>你好，我是布丁。你的 AI 代理。</strong><br/>
  <sub>Hi. I'm Pudding. Your AI agent.</sub>
</p>

---

**一个自包含、支持 P2P 组网的 AI 代理。下载、双击、开始对话。**

[English README](README.md)

![Version](https://img.shields.io/badge/version-v0.1.0-blue)
![Platform](https://img.shields.io/badge/platform-windows%20%7C%20linux%20%7C%20macos-lightgrey)
![License](https://img.shields.io/badge/license-Apache%202.0-green)

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

| 组件 | 技术 |
|---|---|
| 运行时 | .NET (ASP.NET Core, 单文件发布) |
| 数据库 | SQLite — 单文件，自动创建 |
| 前端 | React, 打包嵌入后端 |
| LLM | 直接调用 API（OpenAI 兼容） |
| P2P 网络 | mDNS 发现 + HTTP/gRPC 直连 |
| 记忆 | 本地持久化，私密安全 |

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

## License

Apache License 2.0

---

<p align="center">
  <sub>「……交给我吧。」</sub>
</p>