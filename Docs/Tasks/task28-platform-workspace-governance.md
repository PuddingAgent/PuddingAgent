# task28 - Platform 工作空间治理与服务暴露

最后更新：2026-03-15

## 任务目标

建立 `PuddingPlatform` 的 Workspace 业务层，使平台能够定义服务暴露策略、业务流程编排、审计治理策略和工作空间级能力组合。

对应架构：
- [../07架构/05PuddingPlatform.md](../07架构/05PuddingPlatform.md)
- [../07架构/07协作网络与治理.md](../07架构/07协作网络与治理.md)
- [../07架构/08数据模型与配置.md](../07架构/08数据模型与配置.md)

## 前置依赖

- [task27-controller-routing-session.md](task27-controller-routing-session.md) 已稳定 Controller 基础路由与 Session 入口。
- Workspace、ChannelBinding、AgentTemplate、MemoryPolicy 等基础模型已可读取。

## 可并行关系

- 可与 [task29-agent-template-and-audit.md](task29-agent-template-and-audit.md) 并行推进。
- 可与 [task30-knowledge-infrastructure.md](task30-knowledge-infrastructure.md) 的底层服务设计并行。
- 依赖 Controller 稳定之后才能完成最终联调。

## 顺序任务

1. 建立 `WorkspaceBusinessService`
说明：统一组织 Workspace 下的 Agent、知识能力、服务暴露和业务流程。
输出：Workspace 业务层骨架。

2. 建立 `ServiceExposurePolicy`
说明：定义某个 Workspace 对哪些渠道暴露哪些 Agent、知识能力和服务形态。
输出：服务暴露策略模型与判定接口。
前置依赖：任务 1。

3. 建立 `AuditGovernancePolicy`
说明：确保每个 Workspace 至少 1 个审计 Agent，并定义冻结、批准和监督策略。
输出：审计治理策略骨架。
前置依赖：任务 1。

3A. 建立 Workspace 紧急 stop / recover 治理语义
说明：定义用户和审计 Agent 在检测到入侵、上下文污染或泄露风险时，如何冻结单 Agent 或整个 Workspace，并如何发起恢复。
输出：Workspace 紧急治理语义层、权限边界和审计要求。
前置依赖：任务 3。

4. 建立 Workflow 的平台编排入口
说明：把工作流编排从单纯控制链路上升到平台业务语义层。
输出：Workflow 在 Workspace 业务层的注册与绑定接口。
前置依赖：任务 1；关联 [task25-workflow.md](task25-workflow.md)。

4A. 建立 Workspace 协作驾驶舱语义层
说明：为 Workspace 内的 TaskMap / DAG 主视图、Agent 侧边栏、关键事件流和聚合指标卡提供产品语义与查询聚合接口。
输出：Workspace 协作驾驶舱查询模型与聚合服务接口。
前置依赖：任务 1；关联 `07架构/05PuddingPlatform.md`。

4B. 建立 Workspace 共享工具台语义层
说明：把 TaskBoard、Spreadsheet、Wiki、ObjectStorage、WorkspaceEventHub 等能力组织成统一的 Workspace 虚拟工作台。
输出：共享工具台聚合入口与能力目录。
前置依赖：任务 1、任务 4。

5. 对接 Controller 与配置模型
说明：让 Controller 路由和权限链能读取 Platform 的业务规则输出。
输出：Platform 到 Controller 的规则桥接层。
前置依赖：任务 2、任务 3、任务 4、任务 4A、任务 4B。

## 验收标准

- Platform 能统一承载 Workspace 级业务逻辑。
- 平台能表达服务暴露策略和审计治理策略。
- 平台能表达 Workspace 级紧急 stop / recover 治理语义。
- Platform 规则可被 Controller 消费。
- Workflow 能在 Workspace 语义下被组织和挂接。
- Platform 能提供 Workspace 内的多 Agent 协作驾驶舱语义模型。
- Platform 能将共享任务板、表格、知识和对象存储组织成统一工作台入口。
