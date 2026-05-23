# 07架构

这里存放 Pudding Agent Network 的架构分册。

建议阅读顺序：

1. [01总览与分层](01总览与分层.md)
2. [02PuddingCore](02PuddingCore.md)
3. [03PuddingRuntime](03PuddingRuntime.md)
4. [12多轮会话与工具调用执行](12多轮会话与工具调用执行.md)
5. [04PuddingController与Gateway](04PuddingController与Gateway.md)
6. [10事件系统与事件总线](10事件系统与事件总线.md)
7. [11工作流与任务图](11工作流与任务图.md)
8. [05PuddingPlatform](05PuddingPlatform.md)
9. [06PuddingAgent与客户端](06PuddingAgent与客户端.md)
10. [07协作网络与治理](07协作网络与治理.md)
11. [08数据模型与配置](08数据模型与配置.md)
12. [09V1落地与验收](09V1落地与验收.md)
13. [13记忆与会话数据层](13记忆与会话数据层.md)
14. [12记忆图书馆基础设施](12记忆图书馆基础设施.md)
15. [14消息管线与终端代理与前端优化ADR](14消息管线与终端代理与前端优化ADR.md)
16. [15潜意识LLM子代理系统ADR](15潜意识LLM子代理系统ADR.md)
17. [16会话状态层与客户端解耦ADR](16会话状态层与客户端解耦ADR.md)
18. [17WebSocket连接器与网关鉴权ADR](17WebSocket连接器与网关鉴权ADR.md)
19. [18上下文缓存可观测性ADR](18上下文缓存可观测性ADR.md)
20. [19架构基础设施增强下一步ADR](19架构基础设施增强下一步ADR.md)
21. [20AdminChat简约克制界面ADR](20AdminChat简约克制界面ADR.md)
22. [29ADR-028记忆图书馆基础设施重构ADR](29ADR-028记忆图书馆基础设施重构ADR.md)
23. [32ADR-031聊天历史转录持久化与事件日志回放边界](32ADR-031聊天历史转录持久化与事件日志回放边界.md)

文档分工：

- [../架构.md](../架构.md) 只保留总体定位、分层边界、阅读地图与当前共识。
- 本目录下的文件分别承载模块级说明，避免继续把所有细节堆进单个总文档。

当前目录的共同基线：

- 统一事件总线是 Controller、Gateway、Runtime 与 Agent 协作的主骨架。
- 万物皆事件，但事件域、可见性、订阅权限与执行后果必须受治理约束。
- Gateway 负责把外部协议世界转换为平台事件世界，Runtime 负责把订阅命中变成实际唤醒与执行。
- Workflow / TaskMap 是复杂任务的一等表达，前端可借鉴 FlowGram 风格画布，但运行时语义仍以 Pudding 自身架构为准。
- 若需要继续细化事件命名、Envelope、重放与死信策略，应优先阅读 [10事件系统与事件总线](10事件系统与事件总线.md)。
- **2026-05-03**：Workspace 保留为"场景"分组概念。Chat 为一级入口（顶栏含场景选择器+Agent选择器），场景管理和 Agent 管理退入设置后台。Agent 模板简化为全局模板库。详见 [../架构.md#场景sceneworkspace与-agent-关系模型](../架构.md#场景sceneworkspace与-agent-关系模型)。
