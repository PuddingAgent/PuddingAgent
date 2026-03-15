# task31 - CLI、Avalonia 与控制客户端

最后更新：2026-03-15

## 任务目标

建立客户端层，使 CLI 和 Avalonia 都通过 Controller API 工作，并分别承担调试入口与用户桌面控制端职责。

对应架构：
- [../07架构/06PuddingAgent与客户端.md](../07架构/06PuddingAgent与客户端.md)
- [../07架构/09V1落地与验收.md](../07架构/09V1落地与验收.md)

## 前置依赖

- Controller API 基础契约已稳定。
- Runtime 能返回真实回复。
- Approval 与审计查询接口已具备最小能力。

## 可并行关系

- CLI 与 Avalonia 可以并行推进。
- 客户端调试查询可与 [task32-observability-integration.md](task32-observability-integration.md) 部分并行。
- 最终联调依赖 Controller、Runtime 和模板链路。

## 顺序任务

1. CLI 切换到 Controller API 主链路
说明：CLI 不再直接驱动本地运行时主链路，而是通过平台接口发起消息与会话。
输出：最小 CLI -> Controller -> Runtime 链路。

2. CLI 增加查询与调试命令
说明：支持 Session、Approval、Workflow、路由、审计等查询。
输出：最小控制台管理与调试命令集。
前置依赖：任务 1。

3. CLI 增加批准与 Workspace 冻结控制命令
说明：支持高风险批准、确认码提交、冻结状态查询和审计控制。
输出：CLI 治理操作集。
前置依赖：任务 2。

4. Avalonia 建立基础会话面板
说明：支持登录、查看 Workspace、发起消息、查看 Session 与 Agent 状态。
输出：桌面控制端最小 UI。
前置依赖：任务 1 可并行，但接口依赖 Controller 基础完成。

5. Avalonia 建立语音批准入口
说明：采集语音、提交系统批准请求、绑定 ApprovalRecord。
输出：客户端语音批准入口。
前置依赖：任务 4；依赖 ApprovalService 稳定。

6. Avalonia 建立 Workspace 控制面板
说明：支持请求审计 Agent 冻结 Workspace、查看治理状态与审计反馈。
输出：桌面治理控制面板。
前置依赖：任务 4、任务 5。

## 验收标准

- CLI 能通过 Controller API 发起消息并得到真实回复。
- CLI 能查询路由、Session、审批、审计和 Workflow 状态。
- Avalonia 能作为桌面控制端发起消息、批准和治理操作。
- 语音批准与 ApprovalRecord 可绑定查询。
