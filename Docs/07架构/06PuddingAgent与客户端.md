# PuddingAgent 与客户端

## PuddingAgent

PuddingAgent 用于定义 Agent 是谁，而不是 Agent 当前处于什么运行状态。

### 负责内容

- 角色、人设、系统提示词模板。
- 默认能力组合、工具白名单、策略画像。
- 心跳策略、默认记忆策略、运行画像声明。

### 不负责内容

- Skill 与 MCP 的真实装配执行。
- 插件的安装与治理。
- 会话热状态与执行期上下文。

## 客户端层

客户端层包括 PuddingCLI、PuddingWeb、PuddingAvalonia。

### PuddingCLI

- 管理与调试入口。
- 通过 Controller API 发起消息、查看状态、执行批准。
- 不长期承载 Runtime 状态。

### PuddingWeb

- Web 控制台与可视化面板。
- 展示渠道、会话、Agent、路由、审计与治理状态。
- 通过 Controller 暴露的接口工作。

### PuddingAvalonia

- 用户持有的桌面控制端。
- 承载审批、会话观察、Workspace 控制、语音批准等能力。
- 不直接承载 Agent 运行态。
