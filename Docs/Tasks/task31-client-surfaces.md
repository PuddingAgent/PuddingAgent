# task31 - Web Chat、CLI、Avalonia 与控制客户端

最后更新：2026-03-18

## 任务目标

建立客户端层，使 Web Chat（内置网页聊天渠道）、CLI 和 Avalonia 都通过 Controller API 工作，分别承担**最优先的用户会话入口**、调试入口与桌面控制端职责。

Web Chat 是平台内置的首个真实用户接入渠道，优先级高于 CLI 和 Avalonia。其渠道 ID 采用固定格式 `web-chat-{workspaceId}`，由 `WebChatGatewayAdapter` 统一处理；前端不应自行生成随机 channelId。

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

0. 建立 Web Chat 内置渠道
说明：实现 `WebChatGatewayAdapter`，注册为内置 Adapter；`SeedDefaults()` 为每个 Workspace 自动添加 `web-chat-{workspaceId}` 渠道绑定。前端统一使用该固定 channelId 发起请求，不再自行生成 UUID。同时为 PuddingController 的 CORS 策略补齐前端开发端口（如 `localhost:3000`）。
输出：前端聊天页能以 `web-chat-default` 渠道 ID 成功发起消息并获得回复，不再报 `Channel is not bound to workspace` 错误。

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

---

### P3 渠道扩展（在所有关键链路任务完成后实施）

以下任务依赖主链路（task26~task34）全部收口后再推进，不阻塞当前迭代。

7. 建立飞书（Feishu）渠道 Adapter（P3）
说明：实现 `FeishuGatewayAdapter`，对接飞书机器人 Event Callback API（消息接收）与 OpenAPI（消息发送）。渠道 ID 格式 `feishu-{workspaceId}`；启动时不自动注册到 `SeedDefaults()`，由管理员在 PlatformAdmin 中手动为指定 Workspace 绑定飞书渠道并配置 AppId/AppSecret/EncryptKey/VerificationToken。
输出：飞书渠道可在 Workspace 中注册并正常收发消息。
前置依赖：任务 0（WebChat Adapter 已完成，插件化架构稳定）；飞书应用已在飞书开发者平台创建并获取密钥。

8. 其他第三方渠道扩展（P3，持续迭代）
说明：钉钉、企业微信、Webhook 触发器、MQTT 等均按 `IPuddingGatewayAdapter` 插件化模式实现；每个渠道独立打包，由 PlatformAdmin 按 Workspace 手动绑定。
输出：至少 1 个渠道插件完成端到端冒烟验证。
前置依赖：任务 7（飞书完成后作为范例）。

## 验收标准

**P0**
- Web Chat 前端以 `web-chat-{workspaceId}` 渠道 ID 能成功发起消息并得到真实回复，不报权限错误。

**P2（CLI / Avalonia）**
- CLI 能通过 Controller API 发起消息并得到真实回复。
- CLI 能查询路由、Session、审批、审计和 Workflow 状态。
- Avalonia 能作为桌面控制端发起消息、批准和治理操作。
- 语音批准与 ApprovalRecord 可绑定查询。

**P3（飞书及其他渠道）**
- 飞书机器人能在指定 Workspace 内收发消息，且 channelId 格式正确。
- PlatformAdmin 可手动为 Workspace 绑定 / 解绑飞书渠道并验证渠道状态。
