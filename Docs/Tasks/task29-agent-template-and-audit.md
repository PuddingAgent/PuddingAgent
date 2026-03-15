# task29 - AgentTemplate 与审计 Agent

最后更新：2026-03-15

## 任务目标

建立 `PuddingAgent` 层的模板、权限画像、运行画像和审计模板，确保 Controller、Platform、Runtime 共同围绕同一套模板模型工作。

对应架构：
- [../07架构/06PuddingAgent与客户端.md](../07架构/06PuddingAgent与客户端.md)
- [../07架构/07协作网络与治理.md](../07架构/07协作网络与治理.md)
- [../07架构/08数据模型与配置.md](../07架构/08数据模型与配置.md)

## 前置依赖

- Workspace 边界与 AgentTemplate 模型已稳定。
- Runtime 和 Controller 已有最小执行与路由骨架。

## 可并行关系

- 可与 [task28-platform-workspace-governance.md](task28-platform-workspace-governance.md) 并行推进。
- 可与 [task30-knowledge-infrastructure.md](task30-knowledge-infrastructure.md) 并行推进。
- 联调依赖 [task26-runtime-foundation.md](task26-runtime-foundation.md) 与 [task27-controller-routing-session.md](task27-controller-routing-session.md)。

## 顺序任务

1. 定义最小 `AgentTemplate` 模型
说明：包含角色、人设、默认能力、权限画像、运行画像、默认记忆策略、心跳策略。
输出：稳定模板对象。

2. 提供内置服务模板
说明：至少提供一个对外服务 Agent 模板和一个低风险任务模板。
输出：Controller 可命中的模板集合。
前置依赖：任务 1。

3. 提供 `AuditAgentTemplate`
说明：定义只接触结构化、脱敏、受限视图的审计模板。
输出：可挂接到 Workspace 的审计模板。
前置依赖：任务 1。

4. 定义模板权限与运行画像读取链路
说明：Controller 用于权限判断，Runtime 用于执行环境和能力装配，Platform 用于服务暴露与治理。
输出：统一的模板消费接口。
前置依赖：任务 1。

5. 对接审计链与冻结链
说明：让审计模板参与批准、拒绝、质询与 Workspace 冻结链路。
输出：最小审计 Agent 工作流。
前置依赖：任务 3、任务 4；关联 [task28-platform-workspace-governance.md](task28-platform-workspace-governance.md)。

## 验收标准

- Controller 能命中内置 AgentTemplate。
- Runtime 能依据模板创建 AgentInstance。
- 模板权限与运行画像能被多个层共同读取。
- 每个 Workspace 至少可挂接一个审计模板。
