# 23 运行时可观测性闭环与 E2E 验证基线 ADR

> 状态：**done**（Phase 1-5 完成）
> 日期：2026-05-20
> Git: `67226a7`
> 范围：Runtime timeline、诊断 API、Admin 可观测性、前端 debug mode、Playwright E2E、Docker smoke
> 前置：[19架构基础设施增强下一步ADR](19架构基础设施增强下一步ADR.md)、[20会话状态机与事件规范ADR](20会话状态机与事件规范ADR.md)、[21子代理工作空间与运行归档ADR](21子代理工作空间与运行归档ADR.md)、[22架构基础设施硬化与行动路线ADR](22架构基础设施硬化与行动路线ADR.md)

---

## 1. 背景

ADR-019、ADR-021、ADR-022 已经完成基础设施骨架：

- 配置与目录开始收敛到 `data`。
- 事件系统具备持久化、schema、lease、retry、dead-letter。
- 会话层具备状态机、JSONL 双写、replay、trace-report。
- 子代理具备 workspace/run archive、DB 索引、诊断 API。
- RuntimeActivity 已经覆盖 LLM、事件队列、会话层、工具、子代理等部分链路。

当前剩余问题不是“是否有日志”，而是“是否能稳定回答一次执行到底发生了什么”：

1. 用户发起一次会话后，缺少统一 timeline 把 session、runtime activity、event queue、sub-agent run 串起来。
2. 诊断 API 分散在 runtime、event、session、sub-agent controller 中，字段和过滤方式不统一。
3. Admin UI 缺少面向开发和 QA 的诊断驾驶舱。
4. E2E 缺少标准证据输出：截图、浏览器日志、traceId、后端 timeline、run archive。
5. 前端调试能力分散，缺少只在开发/测试环境启用的稳定测试钩子。

本 ADR 的目标是把现有基础设施收口为“可观测性闭环 + E2E 验证基线”。

---

## 2. 决策

### ADR-023-A：Runtime Timeline 是下一阶段核心抽象

**决定**：新增统一 runtime timeline 视图，聚合 `RuntimeActivity`、事件队列、会话事件、子代理 run archive 的摘要。

Timeline 不是新的主存储。它是诊断查询层：

```text
RuntimeActivityEntity
EventQueueEntity
SessionEventLogEntity / session JSONL
SubAgentRunEntity / run archive
        │
        ▼
RuntimeTimelineQueryService
        │
        ▼
RuntimeTimelineItemDto[]
```

理由：

- 当前数据已经存在，优先做聚合，不引入新的持久化复杂度。
- Admin UI 和 E2E 只依赖稳定 DTO，不直接理解底层表结构或 JSONL 文件。
- 后续 memory、gateway、connector 可继续接入同一 timeline。

### ADR-023-B：诊断 API 必须返回稳定 DTO

**决定**：所有新增诊断接口只返回 DTO，不返回 EF Entity、不返回未收口匿名对象。

核心查询契约：

```csharp
public sealed record RuntimeTimelineQueryDto
{
    public string? WorkspaceId { get; init; }
    public string? SessionId { get; init; }
    public string? TraceId { get; init; }
    public string? AgentInstanceId { get; init; }
    public string? RunId { get; init; }
    public string? Component { get; init; }
    public string? Status { get; init; }
    public DateTimeOffset? FromUtc { get; init; }
    public DateTimeOffset? ToUtc { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 100;
}
```

核心输出契约：

```csharp
public sealed record RuntimeTimelineItemDto
{
    public required string Id { get; init; }
    public required string Kind { get; init; }          // activity/event/session_frame/subagent_run
    public required string Component { get; init; }     // agent_execution/llm_gateway/event_queue/session_state/subagent
    public required string Operation { get; init; }
    public required string Status { get; init; }
    public string? WorkspaceId { get; init; }
    public string? SessionId { get; init; }
    public string? AgentInstanceId { get; init; }
    public string? RunId { get; init; }
    public string? EventId { get; init; }
    public string? TraceId { get; init; }
    public string? CorrelationId { get; init; }
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset? CompletedAtUtc { get; init; }
    public long? DurationMs { get; init; }
    public string? Summary { get; init; }
    public string? Error { get; init; }
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}
```

分页契约沿用现有诊断 DTO：

```csharp
public sealed record PagedResultDto<T>
{
    public required IReadOnlyList<T> Items { get; init; }
    public required int Page { get; init; }
    public required int PageSize { get; init; }
    public required int Total { get; init; }
}
```

### ADR-023-C：诊断入口按用户问题组织，而不是按数据库表组织

**决定**：新增 API 以“我要诊断什么”为入口。

建议 API：

```text
GET /api/diagnostics/runtime/timeline
GET /api/diagnostics/runtime/components
GET /api/diagnostics/sessions/{sessionId}/timeline
GET /api/diagnostics/sessions/{sessionId}/replay-summary
GET /api/diagnostics/subagents/runs
GET /api/diagnostics/subagents/runs/{runId}
GET /api/diagnostics/events
GET /api/diagnostics/events/{eventId}/causation
GET /api/diagnostics/e2e/evidence/{traceId}
```

保留现有 API，但新 UI 和 E2E 优先使用 `/api/diagnostics/*` 命名空间。

兼容策略：

- `RuntimeDiagnosticsController` 可作为底层 activity 查询继续保留。
- `EventDiagnosticsController` 可继续提供事件队列原始查询。
- `SubAgentRunController` 可继续提供 run list/detail。
- ADR-023 新增 controller 负责聚合，不要求一次性删除旧 controller。

### ADR-023-D：Admin 诊断驾驶舱只消费诊断 DTO

**决定**：Admin UI 新增 Diagnostics 区域，第一版优先解决诊断效率，不做复杂 BI。

页面结构：

```text
Diagnostics
  Runtime Timeline
  Session Replay
  SubAgent Runs
  Event Queue
  Component Health
  E2E Evidence
```

第一版能力：

- 按 `sessionId`、`traceId`、`runId`、`component`、`status` 过滤。
- Timeline 展示顺序、状态、耗时、错误摘要。
- 点击 timeline item 展示 metadata 和原始摘要。
- Sub-agent run detail 展示 input、output、events、tools、errors 的摘要。
- Event queue 展示 pending、leased、retrying、dead_letter。
- E2E evidence 页面展示一次自动化测试的截图、traceId、失败原因和后端 timeline 链接。

UI 约束：

- 不直接读取本地文件系统。
- 不在浏览器暴露密钥或完整敏感 payload。
- 不让诊断页面成为业务页面依赖。

### ADR-023-E：前端 Debug Mode 是测试钩子，不是业务逻辑

**决定**：前端增加 debug mode，仅在开发/测试环境启用。

启用方式：

```text
?debug=1
```

或配置：

```json
{
  "frontend": {
    "debugMode": true
  }
}
```

前端全局调试契约：

```ts
export type PuddingDebugApi = {
  getSessionState(sessionId: string): SessionDebugSnapshot | null;
  getLastTraceId(): string | null;
  getLastSessionId(): string | null;
  exportTimeline(): RuntimeTimelineSnapshot | null;
  clearDebugEvents(): void;
};

declare global {
  interface Window {
    __PUDDING_DEBUG__?: PuddingDebugApi;
  }
}
```

DOM 测试契约：

```text
data-testid="chat-input"
data-testid="chat-send"
data-testid="chat-message-list"
data-testid="chat-message-{messageId}"
data-testid="diagnostics-timeline"
data-testid="diagnostics-filter-trace"
data-testid="subagent-run-list"
data-testid="subagent-run-detail"
data-testid="event-queue-list"
```

规则：

- Debug API 只能读状态，不能直接驱动业务流程。
- 自动化测试通过真实 UI 操作触发流程。
- Debug API 用于采集证据和降低失败定位成本。

### ADR-023-F：E2E 使用 Fake LLM + 浏览器自动化 + 后端证据链

**决定**：E2E 基线使用 Playwright，默认走 Fake LLM，不依赖外部 LLM 服务商。

目录结构：

```text
TestScripts/e2e/
  package.json
  playwright.config.ts
  specs/
    chat-smoke.spec.ts
    subagent-run.spec.ts
    diagnostics-timeline.spec.ts
  helpers/
    app.ts
    diagnostics.ts
    fakeLlm.ts
    evidence.ts
  artifacts/
    .gitkeep
```

第一批 E2E 流程：

1. 启动本地服务或连接 `build-and-up.ps1` 启动后的 Docker 服务。
2. 调用 health check。
3. 确认 Fake LLM provider 可用。
4. 创建或打开默认 workspace。
5. 创建会话。
6. 发送一条普通消息。
7. 验证流式响应完成。
8. 触发工具调用。
9. 触发子代理 run。
10. 打开 Diagnostics timeline。
11. 通过 API 拉取 timeline，确认至少包含 session、agent_execution、llm_gateway、subagent 或 event_queue 项。
12. 输出 E2E evidence。

Evidence 契约：

```json
{
  "testName": "chat-smoke",
  "status": "failed",
  "baseUrl": "http://localhost:5000",
  "workspaceId": "default",
  "sessionId": "ses_...",
  "traceId": "trace_...",
  "runId": "run_...",
  "screenshotPath": "TestScripts/e2e/artifacts/chat-smoke/failure.png",
  "browserTracePath": "TestScripts/e2e/artifacts/chat-smoke/trace.zip",
  "backendTimelinePath": "TestScripts/e2e/artifacts/chat-smoke/timeline.json",
  "consoleLogPath": "TestScripts/e2e/artifacts/chat-smoke/console.log",
  "error": "Expected done frame but timed out"
}
```

### ADR-023-G：E2E 与诊断 API 必须共同验收

**决定**：E2E 测试不只断言页面文本，还必须验证后端可观测性证据。

每条核心 E2E 至少断言：

- UI 可完成用户流程。
- 返回或采集到 `sessionId`。
- 返回或采集到 `traceId`。
- `/api/diagnostics/sessions/{sessionId}/timeline` 有数据。
- timeline 中存在失败时的错误摘要，或成功时存在 completed/succeeded 状态。
- 如果触发子代理，则存在 `runId` 且 run detail 可查询。

---

## 3. 设计方案

### 3.1 后端组件

新增组件：

```text
Source/PuddingCore/Diagnostics/
  RuntimeTimelineDtos.cs
  RuntimeTimelineContracts.cs

Source/PuddingPlatform/Services/Diagnostics/
  RuntimeTimelineQueryService.cs
  DiagnosticEvidenceService.cs

Source/PuddingPlatform/Controllers/Api/
  DiagnosticsTimelineController.cs
  DiagnosticsEvidenceController.cs
```

职责：

| 组件 | 职责 |
|------|------|
| `RuntimeTimelineDtos` | 定义稳定 API DTO |
| `RuntimeTimelineQueryService` | 聚合 runtime activity、event queue、session event、sub-agent run |
| `DiagnosticEvidenceService` | 为 E2E 导出 timeline、trace、run 摘要 |
| `DiagnosticsTimelineController` | 提供 `/api/diagnostics/*/timeline` |
| `DiagnosticsEvidenceController` | 提供 E2E evidence 查询与落盘入口 |

聚合顺序：

1. 按 query 读取 `RuntimeActivity`。
2. 读取匹配 trace/session 的事件队列记录。
3. 读取匹配 session 的 session event/replay summary。
4. 读取匹配 session/run 的 sub-agent run index。
5. 统一投影为 `RuntimeTimelineItemDto`。
6. 按 `StartedAtUtc` 升序排序。
7. 分页返回。

### 3.2 API 契约

#### Runtime Timeline

```text
GET /api/diagnostics/runtime/timeline?sessionId=&traceId=&component=&status=&page=1&pageSize=100
```

返回：

```csharp
ActionResult<PagedResultDto<RuntimeTimelineItemDto>>
```

#### Session Timeline

```text
GET /api/diagnostics/sessions/{sessionId}/timeline?page=1&pageSize=100
```

语义：

- 自动过滤 `sessionId`。
- 如果 session replay 中能提取 trace，则合并相关 trace activity。
- 如果 session 中有 sub-agent id/run id，则合并子代理 run 摘要。

#### Component Health

```text
GET /api/diagnostics/runtime/components
```

返回：

```csharp
public sealed record RuntimeComponentHealthDto
{
    public required string Component { get; init; }
    public required string Status { get; init; }      // healthy/degraded/failing/unknown
    public int StartedCount { get; init; }
    public int SucceededCount { get; init; }
    public int FailedCount { get; init; }
    public int RetriedCount { get; init; }
    public int CancelledCount { get; init; }
    public DateTimeOffset? LastSeenAtUtc { get; init; }
    public string? LastError { get; init; }
}
```

#### E2E Evidence

```text
GET /api/diagnostics/e2e/evidence/{traceId}
```

返回：

```csharp
public sealed record DiagnosticEvidenceDto
{
    public required string TraceId { get; init; }
    public string? SessionId { get; init; }
    public string? RunId { get; init; }
    public required IReadOnlyList<RuntimeTimelineItemDto> Timeline { get; init; }
    public IReadOnlyList<SubAgentRunSummaryDto> SubAgentRuns { get; init; } = Array.Empty<SubAgentRunSummaryDto>();
    public IReadOnlyList<EventDiagnosticSummaryDto> Events { get; init; } = Array.Empty<EventDiagnosticSummaryDto>();
}
```

### 3.3 Admin UI

建议新增目录：

```text
Source/PuddingPlatformAdmin/src/pages/diagnostics/
  DiagnosticsPage.tsx
  RuntimeTimelinePage.tsx
  SessionReplayPage.tsx
  SubAgentRunsPage.tsx
  EventQueuePage.tsx
  E2eEvidencePage.tsx
  api.ts
  types.ts
  components/
    TimelineTable.tsx
    TimelineFilters.tsx
    StatusBadge.tsx
    MetadataPanel.tsx
```

UI 第一版采用表格 + 详情侧栏：

- 表格负责快速扫描。
- 详情侧栏展示 metadata、error、payload 摘要。
- 不做复杂图表，避免先消耗 UI 成本。

### 3.4 E2E

Playwright 运行命令：

```powershell
cd TestScripts/e2e
pnpm install
pnpm test
```

Docker smoke 命令：

```powershell
.\build-and-up.ps1
cd TestScripts/e2e
pnpm test:docker
```

E2E 环境变量只允许控制测试目标，不作为系统配置来源：

```text
PUDDING_E2E_BASE_URL=http://localhost:5000
PUDDING_E2E_ARTIFACTS=TestScripts/e2e/artifacts
```

---

## 4. 行动路线

### Phase 1：诊断 DTO 与 Timeline 聚合

任务：

1. 定义 `RuntimeTimelineItemDto`、`RuntimeTimelineQueryDto`、`RuntimeComponentHealthDto`。
2. 实现 `RuntimeTimelineQueryService`。
3. 新增 `DiagnosticsTimelineController`。
4. 增加 unit/integration tests，覆盖 activity/event/sub-agent 聚合。

验收：

- `GET /api/diagnostics/runtime/timeline` 可返回有序 timeline。
- `GET /api/diagnostics/sessions/{sessionId}/timeline` 可按会话聚合。
- 不返回 EF Entity。

### Phase 2：Admin Diagnostics 第一版

任务：

1. 新增 Diagnostics 路由。
2. 实现 Runtime Timeline 页面。
3. 实现 SubAgent Runs 页面复用现有 run DTO。
4. 实现 Event Queue 页面。
5. 增加基本 loading/error/empty 状态。

验收：

- 可按 session/trace/run/status 过滤。
- 能打开一次子代理 run detail。
- 能看到 dead-letter/retrying 事件。

### Phase 3：前端 Debug Mode

任务：

1. 新增 debug mode 判定工具。
2. 注册 `window.__PUDDING_DEBUG__`。
3. 为 Chat 和 Diagnostics 页面增加稳定 `data-testid`。
4. 增加状态快照导出。

验收：

- 非开发/测试环境不暴露 debug API。
- Playwright 能读取 last session/trace。
- 普通业务 UI 不显示 debug 控件。

### Phase 4：Playwright E2E 基线

任务：

1. 创建 `TestScripts/e2e`。
2. 增加 `chat-smoke.spec.ts`。
3. 增加 `subagent-run.spec.ts`。
4. 增加 evidence helper。
5. 失败时输出截图、trace、console log、backend timeline。

验收：

- 一条命令可跑本地 E2E。
- 失败证据可直接定位到 trace/session/run。
- Fake LLM 不依赖外部网络。

### Phase 5：Docker Smoke 与 QA 收口

任务：

1. 将 E2E 接入 `build-and-up.ps1` 或新增 `TestScripts/e2e/run-docker-smoke.ps1`。
2. 增加健康检查等待逻辑。
3. 输出 QA 报告到 `Docs/QA`。
4. 更新 `Docs/Tasks.md` 状态。

验收：

- Docker 启动后可自动验证核心聊天链路。
- QA 报告包含测试命令、结果、证据路径、残余风险。

---

## 5. 测试策略

| 层级 | 覆盖目标 | 建议项目 |
|------|----------|----------|
| Unit | DTO 投影、时间排序、分页、过滤 | `PuddingCoreTests` |
| Integration | Timeline 聚合 activity/event/sub-agent | `PuddingPlatformTests` |
| Web API | `/api/diagnostics/*` 过滤、分页、404、脱敏 | `PuddingWebApiTests` |
| Frontend | Debug mode、diagnostics components | `PuddingPlatformAdmin` |
| E2E | 浏览器真实流程 + 后端证据链 | `TestScripts/e2e` |

必须覆盖：

- 没有 traceId 时按 sessionId 查询。
- 同一 trace 下存在 event + runtime activity。
- 子代理 run terminal 状态展示。
- JSONL 某一行损坏时 detail API 返回部分结果和诊断警告。
- E2E 失败时 evidence 文件生成。

---

## 6. 安全与脱敏

诊断输出必须默认脱敏：

- API key、token、authorization header。
- LLM request 中的 provider secret。
- connector secret。
- 用户明确标记为 sensitive 的 payload 字段。

脱敏契约：

```csharp
public interface IDiagnosticRedactor
{
    string RedactText(string? value);
    IReadOnlyDictionary<string, string> RedactMetadata(IReadOnlyDictionary<string, string> metadata);
}
```

规则：

- DTO 中的 `Metadata` 只允许 string-string。
- 大 payload 默认只返回摘要和长度。
- 需要完整 payload 时必须通过明确的 debug-only API，并受环境开关限制。

---

## 7. 不做事项

本 ADR 不做：

- 不引入 OpenTelemetry Collector、Jaeger、Prometheus 等外部依赖。
- 不重写现有 RuntimeActivity 存储。
- 不把 timeline 作为新的事件主存储。
- 不在业务 UI 中硬编码测试流程。
- 不要求一次性重做所有现有诊断 controller。
- 不在 E2E 中依赖真实 LLM provider。

---

## 8. 验收标准

ADR-023 完成后必须满足：

- 一次普通聊天能在 Diagnostics Timeline 中看到完整执行链。
- 一次子代理调用能从 session timeline 跳转到 run detail。
- 事件队列失败能在 Event Queue 页面看到 retry/dead-letter 状态。
- Playwright E2E 能跑通聊天 smoke。
- E2E 失败能输出截图、browser trace、console log、backend timeline。
- Debug mode 只在开发/测试环境启用。
- 诊断 API 不泄漏密钥。
- 所有新增 API 有测试覆盖。

---

## 9. 任务拆分

| 优先级 | 任务 ID | 标题 | 交付物 |
|--------|---------|------|--------|
| P0 | ARCH-OBS-E2E-001 | Runtime Timeline DTO 与聚合服务 | DTO、query service、排序分页测试 |
| P0 | ARCH-OBS-E2E-002 | Diagnostics Timeline API | `/api/diagnostics/runtime/timeline`、session timeline、component health |
| P0 | ARCH-OBS-E2E-003 | 诊断脱敏服务 | `IDiagnosticRedactor`、metadata 脱敏测试 |
| P1 | ARCH-OBS-E2E-004 | Admin Runtime Timeline 页面 | timeline table、filters、detail panel |
| P1 | ARCH-OBS-E2E-005 | Admin SubAgent/Event 诊断页面收口 | run detail、event queue、dead-letter |
| P1 | ARCH-OBS-E2E-006 | 前端 Debug Mode | `window.__PUDDING_DEBUG__`、test ids、状态快照 |
| P1 | ARCH-OBS-E2E-007 | Playwright E2E 基线 | chat smoke、subagent run、diagnostics timeline |
| P1 | ARCH-OBS-E2E-008 | E2E evidence 输出 | screenshot、trace、console、backend timeline |
| P2 | ARCH-OBS-E2E-009 | Docker smoke 集成 | health wait、Fake LLM、核心链路验证 |
| P2 | ARCH-OBS-E2E-010 | QA 与任务看板收口 | QA 报告、Tasks 状态更新、残余风险 |

