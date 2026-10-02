# PuddingCore

> **2026-05-02**：Core 仍然是共享抽象与协议层。不受部署模型变更影响。

## 定位

PuddingCore 是所有模块共享的核心抽象层，提供协议和通用模型，不持有运行状态。

## 内容

- 抽象接口：LLM、Tool、Skill、Memory 等
- 通用模型：Agent 配置、Session、Message、事件类型
- P2P 协议定义：节点发现、事件广播、任务协作的契约
- 不依赖具体宿主或存储实现

## 依赖关系

PuddingAgent（主程序）和其他模块都依赖 PuddingCore 提供的协议和模型。