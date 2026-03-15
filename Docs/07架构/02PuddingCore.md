# PuddingCore

## 定位

PuddingCore 是所有宿主共享的核心抽象层，提供能力，不持有长期运行状态。

## 主要内容

- 抽象接口：LLM、Tool、Skill、Swarm、Contract、Snapshot 等。
- 通用模型：record、enum、协议对象、事件对象。
- 基础实现：LLM 网关、工具注册、权限守卫等可复用能力。

## 应该放在这里的内容

- ILlmGateway、IToolRegistry、ISkillRegistry 等稳定抽象。
- Swarm 消息模型、契约模型、通用事件类型。
- 不依赖具体宿主生命周期的基础库代码。

## 不应该放在这里的内容

- Agent 运行时状态。
- Session 热状态。
- MCP 会话连接生命周期。
- 插件安装、启停、租户级装配策略。
- 任何要求 Controller 或 Runtime 持久持有的状态。

## 与其他层的关系

- Runtime 依赖 PuddingCore 提供协议和抽象。
- Controller 依赖 PuddingCore 提供控制协议模型。
- Agent 层依赖 PuddingCore 的模板与能力声明模型。
