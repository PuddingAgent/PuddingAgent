# 07架构

这里存放 Pudding Agent Network 的架构分册。

建议阅读顺序：

1. [01总览与分层](01总览与分层.md)
2. [02PuddingCore](02PuddingCore.md)
3. [03PuddingRuntime](03PuddingRuntime.md)
4. [04PuddingController与Gateway](04PuddingController与Gateway.md)
5. [10事件系统与事件总线](10事件系统与事件总线.md)
6. [11工作流与任务图](11工作流与任务图.md)
7. [05PuddingPlatform](05PuddingPlatform.md)
8. [06PuddingAgent与客户端](06PuddingAgent与客户端.md)
9. [07协作网络与治理](07协作网络与治理.md)
10. [08数据模型与配置](08数据模型与配置.md)
11. [09V1落地与验收](09V1落地与验收.md)

文档分工：

- [../架构.md](../架构.md) 只保留总体定位、分层边界、阅读地图与当前共识。
- 本目录下的文件分别承载模块级说明，避免继续把所有细节堆进单个总文档。

当前目录的共同基线：

- 统一事件总线是 Controller、Gateway、Runtime 与 Agent 协作的主骨架。
- 万物皆事件，但事件域、可见性、订阅权限与执行后果必须受治理约束。
- Gateway 负责把外部协议世界转换为平台事件世界，Runtime 负责把订阅命中变成实际唤醒与执行。
- Workflow / TaskMap 是复杂任务的一等表达，前端可借鉴 FlowGram 风格画布，但运行时语义仍以 Pudding 自身架构为准。
- 若需要继续细化事件命名、Envelope、重放与死信策略，应优先阅读 [10事件系统与事件总线](10事件系统与事件总线.md)。
