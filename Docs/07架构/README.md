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
20. [43ADR-042上下文自动压缩与主动Compact命令ADR](43ADR-042上下文自动压缩与主动Compact命令ADR.md)
21. [44ADR-043缓存统计闭环ADR](44ADR-043缓存统计闭环ADR.md)
22. [19架构基础设施增强下一步ADR](19架构基础设施增强下一步ADR.md)
23. [20AdminChat简约克制界面ADR](20AdminChat简约克制界面ADR.md)
24. [29ADR-028记忆图书馆基础设施重构ADR](29ADR-028记忆图书馆基础设施重构ADR.md)
25. [31ADR-030记忆图书馆Page管理器ADR](31ADR-030记忆图书馆Page管理器ADR.md)
26. [48ADR-047记忆图书馆知识图谱演进ADR](48ADR-047记忆图书馆知识图谱演进ADR.md)
27. [49ADR-048Hermes型系统开发方向参考ADR](49ADR-048Hermes型系统开发方向参考ADR.md)
28. [32ADR-031聊天历史转录持久化与事件日志回放边界](32ADR-031聊天历史转录持久化与事件日志回放边界.md)
29. [40ADR-039登录页与Chat视觉二次收敛ADR](40ADR-039登录页与Chat视觉二次收敛ADR.md)
30. [42ADR-041Chat暗色主题语义Token收敛ADR](42ADR-041Chat暗色主题语义Token收敛ADR.md)
31. [51ADR-050会话层统一投影与前端观察者模型ADR](51ADR-050会话层统一投影与前端观察者模型ADR.md)
32. [57ADR-056聊天消息受理与可靠事件流架构ADR](57ADR-056聊天消息受理与可靠事件流架构ADR.md)
33. [58ADR-057前后端可靠SSE与Conversation事件流架构ADR](58ADR-057前后端可靠SSE与Conversation事件流架构ADR.md)
34. [60ADR-059Conversation执行内核与可靠命令链路ADR](60ADR-059Conversation执行内核与可靠命令链路ADR.md)
35. [61ADR-060子代理运行可观测性与会话事件投影ADR](61ADR-060子代理运行可观测性与会话事件投影ADR.md)
36. [62ADR-062前端ChatUI模块化审计与渐进拆分ADR](62ADR-062前端ChatUI模块化审计与渐进拆分ADR.md)
37. [63ADR-063飞书Agent绑定与可靠消息网关ADR](63ADR-063飞书Agent绑定与可靠消息网关ADR.md)
38. [64ADR-064Codex独立执行服务与Pudding自修复重启ADR](64ADR-064Codex独立执行服务与Pudding自修复重启ADR.md)
39. [65ADR-064自进化PuddingAgent vs Hermes Agent对比分析ADR](65ADR-064自进化PuddingAgent%20vs%20Hermes%20Agent对比分析ADR.md)（当前与上一项编号冲突，待统一重编号）
40. [67ADR-066抖音个人开发者评论接入与浏览器自动化ADR](67ADR-066抖音个人开发者评论接入与浏览器自动化ADR.md)
41. [68抖音接入与通用WebView2自动化开发实施规格](68抖音接入与通用WebView2自动化开发实施规格.md)
42. [69PuddingDesktop浏览器工作区运行中心与存储管理实施规格](69PuddingDesktop浏览器工作区运行中心与存储管理实施规格.md)
43. [70Phase2A-1通用BrowserBridge与双标签工作区开发工作指令](70Phase2A-1通用BrowserBridge与双标签工作区开发工作指令.md)
44. [71Phase2A-1验收补丁真实BrowserWorkspace与Bridge可靠性工作指令](71Phase2A-1验收补丁真实BrowserWorkspace与Bridge可靠性工作指令.md)
45. [72Phase2A-1最终验收修复Bridge握手Surface切换与UISmoke工作指令](72Phase2A-1最终验收修复Bridge握手Surface切换与UISmoke工作指令.md)
46. [73Phase2A-1验收证据收口与Phase2A-2准入工作指令](73Phase2A-1验收证据收口与Phase2A-2准入工作指令.md)
47. [74Phase2A-2最小RemoteBrowser与AgentTools实施验收报告](74Phase2A-2最小RemoteBrowser与AgentTools实施验收报告.md)
48. [75Phase2A-3SnapshotLocatorInteractWait开发工作指令](75Phase2A-3SnapshotLocatorInteractWait开发工作指令.md)
49. [76Phase2A-3通用WebView2页面操作实施验收报告](76Phase2A-3通用WebView2页面操作实施验收报告.md)
50. [77Phase2A-3B真实DeepSeekAgent浏览器工具选择验收工作指令](77Phase2A-3B真实DeepSeekAgent浏览器工具选择验收工作指令.md)
51. [78Phase2A-3B外部验收控制器与脱敏BrowserActivity证据开发工作指令](78Phase2A-3B外部验收控制器与脱敏BrowserActivity证据开发工作指令.md)
52. [79Phase2A-3C真实Agent会话WebView2控制闭环开发工作指令](79Phase2A-3C真实Agent会话WebView2控制闭环开发工作指令.md)
53. [80ADR-069MOA子代理设计委员会编排核心ADR](80ADR-069MOA子代理设计委员会编排核心ADR.md)
54. [81ADR-070通用Agent编排图基础架构ADR](81ADR-070通用Agent编排图基础架构ADR.md)
55. [82ADR-071通用Agent编排平台完整设计方案ADR](82ADR-071通用Agent编排平台完整设计方案ADR.md)
56. [83通用Agent编排后端执行内核与ControlPlane施工图](83通用Agent编排后端执行内核与ControlPlane施工图.md)
57. [84通用Agent编排蓝图编辑器与组件系统施工图](84通用Agent编排蓝图编辑器与组件系统施工图.md)
58. [85通用Agent编排交付测试与运维验收图册](85通用Agent编排交付测试与运维验收图册.md)
59. [插件、Hook、Event、Agent FSM 与函数图总架构](../deepseek-harness-pi-plugin-hook-event-architecture-2026-08-14.md)
60. [ADR-072 工作区 TODO、峰谷 Auto 派发与定时任务第一阶段](86ADR-072工作区TODO峰谷Auto派发与定时任务第一阶段ADR.md)
61. [ADR-073 任务看板优先的 Agent 工作台、完整轨迹与实时指标施工方案](87ADR-073任务看板优先的Agent工作台轨迹与实时指标施工ADR.md)

文档分工：

- [../架构.md](../架构.md) 只保留总体定位、分层边界、阅读地图与当前共识。
- 本目录下的文件分别承载模块级说明，避免继续把所有细节堆进单个总文档。

当前目录的共同基线：

- Controller、Gateway、Runtime 与 Agent 通过统一的 Command、Function/Capability、Hook、Event 与 Projection 合同协作；不能把所有调用都压成一种 EventBus 语义。
- “万物皆事件”的精确定义是所有已提交状态变化都产生可治理的事实；同步查询和强类型能力调用仍是 Function，提交前治理使用 Typed Hook。
- 一切可替换业务能力由 Plugin Contribution 提供；built-in 与 third-party 走同一组合、Owner、Scope、权限和生命周期模型。
- Gateway 负责把外部协议世界转换为平台事件世界，Runtime 负责把订阅命中变成实际唤醒与执行。
- Workflow / TaskMap 是复杂任务的一等表达，前端可借鉴 FlowGram 风格画布，但运行时语义仍以 Pudding 自身架构为准。
- Agent、Tool、Graph、Gate、Transform 与 HumanInput 统一为可组合 Function；Agent 生成的可执行任务图使用 `pudding.agent-orchestration/v2` 声明式契约，经过编译、策略、预算、审批和不可变 Revision 冻结后运行，MOA 是模板实例。当前定义、修订、运行、事件边界和 replay-to-live 基础见 [ADR-070](81ADR-070通用Agent编排图基础架构ADR.md)，完整目标及逐层施工/验收见 [ADR-071 文档包](82ADR-071通用Agent编排平台完整设计方案ADR.md)。
- 产品施工顺序、完整任务目标、优先级、工作量、难度和跨文档冲突裁决以 [ADR-073](87ADR-073任务看板优先的Agent工作台轨迹与实时指标施工ADR.md) 为准：先完成五列任务看板闭环，再做 Auto/Cron、完整轨迹和实时指标。
- 工作区 TODO、手工/Auto 派发、受限 Cron 定时消息和峰谷调度的任务领域合同以 [ADR-072](86ADR-072工作区TODO峰谷Auto派发与定时任务第一阶段ADR.md) 为准；该阶段明确排除质询器、Goal、自动提醒、完整审批和 Agent 生成图。
- 若需要继续细化事件命名、Envelope、重放与死信策略，应优先阅读 [10事件系统与事件总线](10事件系统与事件总线.md)。
- 若需要研究 token 成本、前缀缓存命中、工具输出/日志/RAG 进入 LLM 前压缩和 Headroom 参考路线，应优先阅读 [18上下文缓存可观测性ADR](18上下文缓存可观测性ADR.md)、[43ADR-042上下文自动压缩与主动Compact命令ADR](43ADR-042上下文自动压缩与主动Compact命令ADR.md) 与 [44ADR-043缓存统计闭环ADR](44ADR-043缓存统计闭环ADR.md)。
- 若需要讨论 Hermes 型系统的 1~7 开发方向、优先级和待细化问题，应优先阅读 [49ADR-048Hermes型系统开发方向参考ADR](49ADR-048Hermes型系统开发方向参考ADR.md)。
- 若需要实现 Windows 桌面宿主、Agent 通用 WebView2 控制或个人账号抖音评论接入，应先阅读 [67ADR-066](67ADR-066抖音个人开发者评论接入与浏览器自动化ADR.md)，再按 [68开发实施规格](68抖音接入与通用WebView2自动化开发实施规格.md) 分阶段开发；Browser Workspace、Core/Desktop Bridge、运行中心、开发脚本边界和 Storage 页面按 [69实施规格](69PuddingDesktop浏览器工作区运行中心与存储管理实施规格.md) 执行。Phase 2A-1 的初始包和两轮修复见 [70](70Phase2A-1通用BrowserBridge与双标签工作区开发工作指令.md)、[71](71Phase2A-1验收补丁真实BrowserWorkspace与Bridge可靠性工作指令.md)、[72](72Phase2A-1最终验收修复Bridge握手Surface切换与UISmoke工作指令.md)，最终验收证据见 [73](73Phase2A-1验收证据收口与Phase2A-2准入工作指令.md)。Phase 2A-2 最小 Remote Browser 与三项 Agent Tools 见 [74](74Phase2A-2最小RemoteBrowser与AgentTools实施验收报告.md)；Phase 2A-3 Snapshot/Locator/Interact/Wait 的契约与自动验收见 [75](75Phase2A-3SnapshotLocatorInteractWait开发工作指令.md)、[76](76Phase2A-3通用WebView2页面操作实施验收报告.md)。真实 Agent 会话的 capability、调用来源、自动控制状态与 ToolExecution-to-Bridge 组合测试按 [79](79Phase2A-3C真实Agent会话WebView2控制闭环开发工作指令.md) 实现；真实 DeepSeek 工具选择验收按 [77](77Phase2A-3B真实DeepSeekAgent浏览器工具选择验收工作指令.md) 执行，外部控制器与安全证据见 [78](78Phase2A-3B外部验收控制器与脱敏BrowserActivity证据开发工作指令.md)。该 smoke 通过后才进入 Douyin Adapter；Playwright 仅作为模型熟悉的交互语义参考，Douyin 只能依赖通用 Browser Abstractions。
- **2026-05-03**：Workspace 保留为"场景"分组概念。Chat 为一级入口（顶栏含场景选择器+Agent选择器），场景管理和 Agent 管理退入设置后台。Agent 模板简化为全局模板库。详见 [../架构.md#场景sceneworkspace与-agent-关系模型](../架构.md#场景sceneworkspace与-agent-关系模型)。
