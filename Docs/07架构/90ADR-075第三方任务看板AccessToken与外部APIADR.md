# ADR-075：第三方任务看板 Access Token 与外部 API

> 状态：Proposed
> 日期：2026-08-21
> 决策范围：第三方任务看板认证、授权、外部 API、任务评价与 Admin Token 管理
> 详细设计：[第三方任务看板 Access Token 与外部 API 详细设计方案](../Features/第三方任务看板AccessToken与外部API详细设计方案.md)
> 实施状态：未开始；本 ADR 的存在不表示代码、数据库、配置或生产入口已经落地。

## 1. 背景

PuddingAgent 已有五列 Workspace Task Board、Task Ledger、CAS 状态更新、评论、Command 与 SSE Watch。当前 HTTP API 统一使用 Admin 登录 JWT；JWT 面向交互式用户会话，不具备第三方凭据所需的按 Token scope、workspace allow-list、独立到期、撤销、最后使用和审计。

新的目标是：

1. Admin 能管理面向第三方的 Access Token；
2. 第三方通过 Token 安全调用任务看板；
3. 第三方可读取、创建/导入、修改、评论、评价 Task；
4. 保持现有 Task 状态机、Task Ledger、Admin JWT 与 Runtime `task_*` 工具为唯一权威。

## 2. 决策驱动因素

- 使用 ASP.NET Core 现成的认证 scheme、Authentication Handler、Authorization Policy 与 Rate Limiter；
- 不把 Secret 明文存盘或放进 URL/日志；
- 最小权限、workspace 隔离、即时撤销；
- 第三方合同可版本化，不能把内部未版本化 API 永久冻结为外部合同；
- 任何外部写操作复用现有 CAS、状态机、Store 与事件；
- 评价与状态变更分离；
- 避免在 SQLite 认证热路径产生每请求写竞争；
- 设计完成与实现、部署、生产接受必须分开报告。

## 3. 决策

### 3.1 选择 opaque External Access Token

第三方凭据采用高熵 opaque token：

```text
pdt_v1_<keyId>.<256-bit-secret>
```

数据库只保存 canonical token 的 SHA-256 摘要。明文仅在创建成功响应中返回一次，之后不可恢复。Token 必须到期，默认 90 天、最大 365 天；撤销不可逆。

代码统一命名 `ExternalAccessToken`，避免与当前 Admin JWT 混淆。

### 3.2 保留 JWT 默认方案，增加独立认证 scheme

当前 JWT Bearer 保持默认 scheme。新增 `PuddingExternalAccessToken` scheme，通过 `AuthenticationHandler<ExternalAccessTokenOptions>` 解析和验证 opaque token。

不使用全局字符串猜测或 Policy Scheme 转发：

- Internal/Admin API 使用 JWT；
- `/api/external/v1/**` Policy 显式使用 External Token scheme；
- `/api/admin/access-tokens/**` 显式锁定 JWT scheme + admin role；
- External Token 永远不获得 admin role。

ASP.NET Core 官方支持注册唯一名称的多个 scheme，并由授权 Policy 选择 scheme；本方案使用框架扩展点，不在 Action 中手写认证。[Authorize with a specific scheme](https://learn.microsoft.com/en-us/aspnet/core/security/authorization/limitingidentitybyscheme?view=aspnetcore-10.0)

### 3.3 使用 scope + workspace 的 Policy 授权

冻结 scope：

```text
tasks.read
tasks.write
tasks.comment
tasks.evaluate
tasks.command
```

每个 External Policy 必须同时满足：

1. `PuddingExternalAccessToken` scheme 已认证；
2. 包含端点要求的 scope；
3. route `workspaceId` 位于 Token workspace allow-list。

不存在 `*` scope 或 global workspace。V1 不提供第三方硬删除。`tasks.write` 不隐含 `tasks.command`；`tasks.evaluate` 不隐含状态迁移。

Policy 采用 requirement/handler 模型。[Policy-based authorization](https://learn.microsoft.com/en-us/aspnet/core/security/authorization/policies?view=aspnetcore-10.0)

### 3.4 External API 与 Internal API 分离，但共享应用服务

现有 Internal API：

```text
/api/workspaces/{workspaceId}/tasks/**
```

继续服务 Admin JWT。第三方稳定合同为：

```text
/api/external/v1/workspaces/{workspaceId}/tasks/**
```

External Controller 仅负责 versioned DTO、Policy、Actor/Origin、ETag、Idempotency-Key、ProblemDetails 与 OpenAPI 适配。业务操作必须下沉/复用同一个 Task application service、`TaskCommandService`、`ITaskStore`、`TaskStateMachine` 和 `task_events`。

禁止建立第二套 `external_tasks`、第二套状态枚举、第二个 Task DbContext 或兼容双写。

### 3.5 External mutation 使用 ETag 与 durable idempotency

- PATCH/Command 要求 `If-Match: "task-v{version}"`；
- External Controller 将 ETag 映射到现有 `expectedVersion`；
- 缺前置条件返回 428，版本冲突返回 412；
- create/comment/evaluate/command 要求 `Idempotency-Key`；
- 同 key 同 body 重放原结果，同 key 不同 body 返回 409；
- External V1 不同时接受 body `expectedVersion`，避免双事实源。

### 3.6 评价是一等追加式子资源

新增 `TaskEvaluation`：

- verdict：`accepted | needs_changes | rejected`；
- score：1-5；
- comment：必填；
- `taskVersionObserved`：评价者观察到的 Task version；
- evaluator：从 External Token actor 生成；
- correction：新评价通过 `supersedesEvaluationId` 指向旧评价。

评价写入 `task_evaluations` 与 `task.evaluated` 事件，二者原子提交。评价不修改 Task status，不增加 Task aggregate version，不触发 command。Archived Task 不接受新评价。

### 3.7 Token 管理只允许 JWT Admin

Admin 页面位于：

```text
/system-config/access-tokens
```

后端提供 list/create/detail/rename/revoke，不提供 reveal、unrevoke、硬删除或原地扩大 scope/workspace。创建成功后以一次性 Modal 显示 Secret；关闭或刷新后不可恢复。

### 3.8 Token 存储和审计属于数据库，策略属于配置

数据库表：

```text
external_access_tokens
external_access_token_scopes
external_access_token_workspaces
external_access_token_audit_events
external_api_idempotency
task_evaluations
```

`ExternalTaskApi` Enabled、PublicBaseUrl、HTTPS、Token 生命周期、限流和保留期进入 `<DataRoot>/config/system.json` 强类型配置。Secret 不进入配置。

### 3.9 `last_used_at` 使用合并写

Authentication Handler 的正确性路径只读数据库；成功请求将 last-used 通知写入有界合并器，同一 Token 最多每 5 分钟 UPDATE 一次。展示时间允许最多 5 分钟误差，撤销和认证正确性不允许误差。

### 3.10 使用 ASP.NET Core Rate Limiter

External API 按认证后的 tokenId 分区，分别限制 REST、mutation concurrency 和 SSE connection。`UseRateLimiter` 位于 Authentication 之后、Authorization 之前，以便使用 token claim 分桶。非 Loopback 明文 HTTP 默认拒绝。

ASP.NET Core Rate Limiter 支持按用户/API key/endpoint 等 partition key 分桶。[Rate limiting middleware](https://learn.microsoft.com/en-us/aspnet/core/performance/rate-limit?view=aspnetcore-10.0)

## 4. API 决策表

| 能力 | External V1 route | Scope |
|------|-------------------|-------|
| Token 自检 | `GET /api/external/v1/token` | authenticated |
| Task list/get/watch | `/workspaces/{workspaceId}/tasks...` | `tasks.read` |
| Task create/metadata patch/import | `/workspaces/{workspaceId}/tasks...` | `tasks.write` |
| Comment append | `.../{taskId}/comments` | `tasks.comment` |
| Evaluation append | `.../{taskId}/evaluations` | `tasks.evaluate` |
| 状态/执行命令 | `.../{taskId}/commands/{command}` | `tasks.command` |
| Hard delete | 不提供 | 无 |
| Token management | `/api/admin/access-tokens` | JWT admin only |

## 5. 安全不变量

1. 数据库、日志、审计、Trace、ProblemDetails 和服务端配置中不存在 Token 明文。
2. Secret 只显示一次；不能 reveal。
3. External Token 不能进入 Admin Token 管理或任何非 External Task API。
4. scope 和 workspace 均由服务端 Policy 计算，客户端字段不能覆盖。
5. External actor 固定为 `access-token:{tokenId}`，origin 固定为 `external.api`。
6. 所有 mutation 复用 Task 状态机/CAS；外部不能直接写 status/event/version。
7. 评价不改变 Task 状态。
8. 跨 workspace 资源返回 404，避免枚举。
9. Token 不出现在 query string；SSE 也只接受 Authorization Header。
10. owner 禁用/删除、到期或撤销均 fail closed。
11. External API 默认关闭；非 Loopback 必须 HTTPS。

## 6. 被拒绝的方案

### 6.1 复用 Admin 登录 JWT

拒绝。JWT 面向交互登录，生命周期短且按用户，而不是按集成；缺少逐 Token workspace/scope/revoke/last-used 管理。让第三方保存 Admin 密码或长期 JWT 会扩大事故面。

### 6.2 让 Access Token 也是自包含 JWT

拒绝作为 V1。自包含 JWT 对逐 Token 即时撤销、权限缩小和最后使用审计不友好；最终仍需服务端状态表。Opaque token 更直接。

### 6.3 在 TaskController 中手写 `Authorization` Header 判断

拒绝。会绕开 ASP.NET Core authentication/authorization challenge、policy、测试和 scheme 隔离，并把安全逻辑复制到每个 Action。

### 6.4 让同一 Internal Task route 同时接受 JWT 和 Access Token

拒绝。内部未版本化 DTO 会被第三方永久绑定，JWT 与 Token 权限语义易混合，也难以提供外部专用 ETag、Idempotency 与 ProblemDetails 合同。

### 6.5 评价复用 Task Comment

拒绝。评论没有 score、verdict、观察版本、correction 链或明确审计语义；混用会迫使消费者解析自然语言。

### 6.6 评价自动完成或重开任务

拒绝。评价是反馈事实，Task 状态只能由结构化 Command/Disposition 推进。自动联动会绕过 CAS、scope 与状态机。

### 6.7 实现完整 OAuth/OIDC Server

暂不选择。当前需求是 Admin 创建机器凭据，不是第三方用户委托授权。未来出现多租户开发者平台、授权同意页、refresh token 或标准客户端注册时另立 ADR。

### 6.8 每次认证同步更新 last-used

拒绝。会在 SQLite writer 上制造高频非关键写入。V1 采用有界合并写。

## 7. 后果

### 7.1 正面后果

- 使用 ASP.NET Core 原生安全管线，可单元/集成测试；
- 第三方权限最小化且可即时撤销；
- Internal/Admin 与 External 稳定合同互不污染；
- Task 状态仍只有一个权威；
- 评价成为可查询、可审计的结构化事实；
- 限流与合并写适合当前单 Core + SQLite 运行形态。

### 7.2 成本与负面后果

- 增加第二认证 scheme、六张表、外部 DTO/Controller 和 Admin 页面；
- External/Internal Controller 需要共享应用服务抽取，初期有重构成本；
- Opaque Token 每个请求需要索引查询；V1 选择即时撤销优先，不做正向认证缓存；
- 外部 API 需要独立 OpenAPI、版本和兼容维护；
- `last_used_at` 是近似时间，不是逐请求精确账本。

## 8. 实施顺序

1. 冻结 external scope/DTO/错误/ETag/evaluation/OpenAPI；
2. Token tables/store/service；
3. AuthenticationHandler + policies + tests；
4. Admin Token API；
5. External Task API read/write；
6. comments/evaluations/commands；
7. idempotency/RateLimiter/SSE/revocation；
8. Admin Token UI；
9. HTTPS 部署、真实 External API smoke、回归与外部验收。

详细目标文件和分期见完整设计 §13-§15。

## 9. 接受门槛

### 9.1 安全

- Token 明文 Secret Scanner、数据库检查、日志检查均为 0 命中；
- malformed/unknown/expired/revoked/owner-disabled 统一 401；
- scope/workspace 矩阵和 JWT/External scheme 隔离测试通过；
- revoke 下一请求立即失败，SSE 在 heartbeat 内关闭；
- 非 Loopback HTTP 被拒。

### 9.2 业务一致性

- External/Internal/Runtime tools 操作同一 Task Ledger；
- 无第二状态机或双写；
- ETag 映射现有 CAS；
- Idempotency replay 不重复提交；
- evaluation 不改变 Task status/version；
- SSE snapshot/replay/live 等价。

### 9.3 Admin UI 与外部合同

- Admin Secret 只显示一次；
- 创建默认最小 scope；
- revoke/replace 流程完整；
- External OpenAPI v1 snapshot 已评审；
- 集成 smoke 覆盖 read/write/comment/evaluate/command/watch；
- 401/403/412/428/429 合同均有集成测试覆盖。

### 9.4 产品验收

- 后端、前端和集成测试通过不等于已投产；
- 新构建由进程外控制器部署；
- 新 Pudding 会话执行真实 functional smoke；
- 最终 Core/Desktop 生命周期与外部 HTTPS 入口由外部控制器验证；
- 分别记录 `ready-for-external-deploy`、`in-product-functional-complete` 和最终生产接受证据。

## 10. 回滚

- `ExternalTaskApi.Enabled=false` 关闭所有 external endpoints；
- 可批量 revoke active Token；
- 不删除 Token、evaluation、idempotency 和 audit 事实；
- Internal JWT Task API 与 Runtime tools 不依赖外部 feature flag；
- 已提交 Task/evaluation 不回滚；
- 破坏性合同变化通过 `/v2` 处理，不在 V1 加猜测兼容层。

## 11. 与既有 ADR 的关系

- 延续 [ADR-072](86ADR-072工作区TODO峰谷Auto派发与定时任务第一阶段ADR.md)：`WorkspaceTask` 独立于 TaskPlan/Graph，状态通过结构化命令/CAS 推进。
- 延续 [ADR-073](87ADR-073任务看板优先的Agent工作台轨迹与实时指标施工ADR.md)：Task Board 是先行产品切片，Snapshot + Watch 与稳定 ID 不变量不变。
- 延续 [任务看板施工合同冻结 v1](88任务看板施工合同冻结v1.md)：前端不自造业务状态，后端投影是唯一事实解释者。
- 本 ADR 不改变 [ADR-074](89ADR-074Goal持久目标自主续行与自动压缩ADR.md) 的 Goal/continuation 边界；External Task Command 不能绕过 Goal、Fence、Assignment 或现有运行准入。

## 12. 本 ADR 冻结的最终决策

- External Token 是 hashed opaque PAT，不是 Admin JWT；
- ASP.NET Core 第二 scheme + Policy 是唯一认证授权入口；
- Token 管理锁定 JWT Admin；
- External API 使用 `/api/external/v1`；
- Internal/External 共享同一 Task application service；
- scope 与 workspace 双门禁，无通配符；
- 外部无 hard delete；
- ETag + Idempotency-Key 是 mutation 必需合同；
- 评价为追加式一等资源，不改 Task 状态；
- Token Secret 只显示一次且不进入数据库、日志、配置或 URL；
- last-used 合并写；
- per-token Rate Limit；
- 非 Loopback HTTPS；
- 本轮只完成文档设计，后续必须逐阶段实现和验收。
