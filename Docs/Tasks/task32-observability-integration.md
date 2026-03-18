# task32 - 可观测性、集成验收与阶段收口

最后更新：2026-03-15

## 任务目标

把 Runtime、Controller、Platform、客户端、知识能力和审计治理串成可查询、可调试、可验收的完整平台链路。

对应架构：
- [../07架构/09V1落地与验收.md](../07架构/09V1落地与验收.md)
- [../07架构/07协作网络与治理.md](../07架构/07协作网络与治理.md)

## 前置依赖

- [task26-runtime-foundation.md](task26-runtime-foundation.md)
- [task27-controller-routing-session.md](task27-controller-routing-session.md)
- [task28-platform-workspace-governance.md](task28-platform-workspace-governance.md)
- [task29-agent-template-and-audit.md](task29-agent-template-and-audit.md)
- [task30-knowledge-infrastructure.md](task30-knowledge-infrastructure.md)
- [task31-client-surfaces.md](task31-client-surfaces.md)
- [task34-event-bus-and-subscription.md](task34-event-bus-and-subscription.md)

## 可并行关系

- 审计事件、运行指标、调试查询三条线可并行推进。
- 集成验收项需要在各子系统具备最小稳定版本后再统一串联。

## 顺序任务

1. 建立首批审计事件落盘
说明：记录渠道消息进入、Session 创建/复用、路由决策、审批请求与结果、工具执行、Workflow Step、记忆写入/提升、Workspace 冻结。
输出：AuditStore 可查询事件流。

2. 建立首批运行指标
说明：统计请求量、Session 数、Agent 活跃数、审批等待数、Workflow 成败、模型/工具成本、Runtime 负载、事件吞吐、订阅命中率、死信数量。
输出：最小指标采集与查询面。
前置依赖：任务 1 可并行，不强依赖。

3. 建立首批调试查询能力
说明：支持查看消息命中的 Agent、动作拒绝原因、Workflow 卡点、Session 权限快照、记忆写入拦截原因。
输出：调试查询接口。
前置依赖：任务 1。

4. 执行首条垂直切片集成验收
说明：验证 CLI / Avalonia -> Controller API -> Workspace 路由 -> ServiceSession -> Runtime Agent -> 真实 LLM 回复。
输出：首条真实链路验收结果。
前置依赖：任务 3。

5. 执行审批、知识访问、审计冻结、Workflow 最小链路验收
说明：分别验证高风险审批链、知识访问链、Workspace 冻结链、2-3 步 Workflow。
输出：阶段验收报告。
前置依赖：任务 4。

6. 执行事件驱动链路验收
说明：验证至少 1 条外部事件（例如 MQTT / Webhook）→ Gateway → Workspace 域事件流 → Runtime/Agent 订阅 → 直接唤醒的完整链路，并验证失败事件进入死信隔离或审计记录。
输出：事件链路验收报告。
前置依赖：任务 5。

## 验收标准

- 平台至少具备审计事件、运行指标、调试查询三类观测面。
- 首条垂直切片可以真实跑通。
- 高风险审批、知识访问、审计冻结和最小 Workflow 可分别验收。
- 至少 1 条事件驱动接入与直接唤醒链路可验收，且事件吞吐、命中、失败隔离可观测。
- 文档和任务状态能对应到源码实际进展。
