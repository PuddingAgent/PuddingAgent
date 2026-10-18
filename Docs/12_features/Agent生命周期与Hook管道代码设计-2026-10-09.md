---
title: Agent 生命周期与 Hook 管道代码设计
author: hyfree
date: 2026-10-09
last_reviewed: 2026-10-18
status: proposed
description: "定义中央 C# 注册入口、BCL 契约、注册快照、生命周期协调器、过滤器、动作和时间调度的目标代码布局、算法及迁移验收。"
categories: [docs, features]
tags: [agent, lifecycle, hooks, code-design, composition, events, actions]
related_docs: [Docs/07_architecture/112ADR-098Core事件驱动与严格分层基础设施ADR.md, Docs/12_features/Core事件驱动基础设施施工与代码设计-2026-10-18.md, Docs/12_features/Agent全生命周期Hook事件与动作注册点目录-2026-10-09.md, Docs/12_features/Agent生命周期与Hook管道架构设计-2026-10-09.md, Docs/14_reports/2026-10-09-Agent生命周期与Hook边界审计.md, Docs/10_conventions/组件化交付规程.md]
related_files: [Source/PuddingRuntime/Services/AgentExecutionService.cs, Source/PuddingRuntime/Services/AgentLoop/IAgentLoopHook.cs, Source/PuddingRuntime/Tools/Platform/PuddingToolRegistry.cs, Source/PuddingHost/PuddingHost.csproj]
slug: features-agent-lifecycle-hook-pipeline-code-design-2026-10-09
draft: false
---

# Agent 生命周期与 Hook 管道代码设计

## 1. 实施依据与范围

优先评审[全生命周期注册点目录](Agent全生命周期Hook事件与动作注册点目录-2026-10-09.md)：它规定哪些阶段允许注册什么。本文给出足以分解施工任务的项目、文件、类型、接口、顺序、算法和测试合同。代码块是**目标 API 及算法示例**，不是已提交的可运行实现；示例中的端口/DTO 都按下文类型清单交付。

本轮只交付文档。后续实施遵守[组件化交付规程](../10_conventions/组件化交付规程.md)，独立 S1–S4 通过前不修改 Host、Runtime、解决方案或现有 DI。

与事件基础设施的边界以 [ADR-098](../07_architecture/112ADR-098Core事件驱动与严格分层基础设施ADR.md) 及其[施工方案](Core事件驱动基础设施施工与代码设计-2026-10-18.md)细化：可靠事件受理、Delivery 和持久消费者归 Eventing；本方案的 ObserverMailbox 只负责允许丢失的在线观察。目标 Kernel 仅引用 Contracts，通过 IHookPipeline 调用已装配管道，不直接引用 Pipeline 实现；下列其他生命周期与工具接缝继续适用。

## 2. 项目与目标文件布局

```text
Source/
  PuddingAgentKernel.Contracts/       net10.0；Microsoft.NET.Sdk；BCL-only
    Lifecycle/{AgentSessionState,RunState,RunOutcome,TransitionRequest}.cs
    Identity/{InvocationIdentity,OwnerIdentity,ScopeKey}.cs
    Extensions/{AgentExtensionPointCatalog,RegistrationDescriptor}.cs
    Hooks/{RuntimeHooks,SessionHooks,RunHooks,ContextHooks,LlmHooks}.cs
    Hooks/{ToolHooks,ReplyHooks,DelegationHooks,PluginHooks}.cs
    Events/{AgentEventEnvelope,EventPayloads,IAgentEventObserver}.cs
    Actions/{ActionDescriptor,ActionEnvelope,ActionResult,IActionHandler}.cs
    Time/{ScheduleDefinition,TriggerEnvelope,ITimeTriggerStore}.cs
    Ports/{IAgentExecutionBody,IRunCommitPort,IToolExecutorPort}.cs
    Ports/{IPermissionPort,IExtensionDiagnosticsPort,IBackgroundJobPort}.cs
    code_map.md
  PuddingInvocationPipeline/         只引用已交付 Contracts
    Registry/{AgentExtensionRegistry,RegistrationTransaction}.cs
    Registry/{ExtensionSnapshot,SnapshotLease,OwnerLeaseTracker}.cs
    Execution/{ToolInvocationPipeline,HookCallBoundary,OnceNextGate}.cs
    Execution/{ActionDispatchPipeline,ActionRouteIndex,FilterChainCompiler}.cs
    Events/{ObserverDispatcher,ObserverMailbox,ObserverSubscription}.cs
    Diagnostics/{ExtensionDiagnosticsQuery,HookFailureRecorder}.cs
    code_map.md
  PuddingAgentKernel/                只引用 Contracts；管道经 IHookPipeline 端口
    Lifecycle/{AgentSessionCoordinator,RunCoordinator,RunFinalizer}.cs
    Lifecycle/{LifecycleTransitionTable,NecessaryWorkLedger}.cs
    Execution/{AgentActionRouter,RoundCoordinator}.cs
    code_map.md
  PuddingToolPolicies/               只引用 Contracts
    Authorization/{CapabilityFilter,WorkspaceFilter,SandboxFilter}.cs
    Authorization/{ToolApprovalFilter,ResourcePolicyFilter}.cs
    Ports/                            策略需要的窄端口；不暴露 DbContext
    code_map.md
  PuddingTimeTriggers/               只引用 Contracts
    {TimeTriggerCoordinator,SchedulePlanner,TriggerDispatcher}.cs
    code_map.md
  <上述各组件>Tests/                  各测试工程只直接引用自身组件
  PuddingHost/Composition/           仅 S5 时新增或修改
    AgentExtensionsComposition.cs    唯一生产注册声明入口
    AgentExtensionsBuilder.cs        DI/工厂适配，不进入内核契约
    Extensions/*Extensions.cs        初版消费方侧扩展方法
    Adapters/{LegacyFirewallFilter,LegacyLoopObserverAdapter}.cs
    Adapters/{LegacyRunBodyAdapter,ExistingRunCommitAdapter}.cs
```

事件持久 outbox、记忆仓储、渠道投影、插件 Worker 与 IPC 属于各自基础设施组件，不能添加到 Kernel。TimeTriggers 在 Contracts/Pipeline 独立交付之后开发，时间模块只提交受控动作命令。各组件测试只直接引用自身工程；其传递引用闭包必须符合上述图，不引入宿主或其他重依赖。

## 3. 基础 DTO 与不变量

| 类型 | 必需字段/约束 |
|---|---|
| InvocationIdentity | RuntimeGeneration、AgentSessionId、ExecutionId、可选 RoundId/CallId、ActorId、WorkspaceId、CausationId；来自可信入口；scope 验证不信任 payload |
| RegistrationDescriptor | Id、OwnerIdentity、PointId、ContractTypeId、Kind、Scope、Order、Sequence、Lifetime、Trust、Timeout、FailurePolicy、Required、ConfigVersion |
| ExtensionPointDefinition | PointId、Stage、允许合同类型、Kind、Scope 集合、错误政策、PayloadSchemaVersion；单一静态 Catalog |
| ToolCallRequest | CallId、ToolName、ArgumentsJson、Identity、期望绑定版本；原始请求只读 |
| PreparedToolCall | 已解析 descriptor、规范化参数、ArgumentsHash、调用 scope；不暴露服务对象 |
| ToolCallContext | PreparedToolCall、DeadlineUtc、取消/预算视图、RegistryGeneration；输入授权后不可变 |
| ToolExecutionResult | 原始执行状态、EffectState、安全 FailureInfo、内容 artifact reference/hash、usage；由终端返回并封存 |
| ToolCallOutcome | 调用 Outcome、原始 ToolExecutionResult（执行前为空）、模型可见 ResultProjection、投影状态、审批/策略证据、span 时长 |
| ResultProjection | 不可变内容 parts/summary、artifact refs、redaction 标记；不含可写原始 outcome |
| HookDecision | Allow / Deny / RequireApproval / Defer、ReasonCode、EvidenceRef；Decision 不具有写 Agent 状态的方法 |
| RunOutcome | Done / Waiting / Failed / Cancelled / TimedOut / BudgetExhausted / AdmissionRejected / Deferred / InfrastructureFailure |
| FailureInfo | Code、Stage、SafeMessage、EvidenceRef、RetryAdvice；不向外传 Exception 实例 |

DTO 用 sealed record + 不可变成员/ImmutableArray；string JSON 进入时按大小限制解析和复制，JsonElement 必须 Clone，不能留下已 Dispose 的 JsonDocument 引用。集合 IReadOnlyList 本身不保证底层不可变，因此不能直接暴露可变 List。

身份、许可 token、状态版本与 effect ledger 不由 middleware 修改。internal 构造/封装只能降低误用；跨进程的真正授权依赖入口验证和能力服务逐请求检查，不能以 C# private 当安全沙箱。

## 4. 控制与观察接口

### 4.1 工具过滤器

```csharp
public enum HookDisposition { Allow, Deny, RequireApproval, Defer }
public sealed record HookDecision(
    HookDisposition Disposition, string ReasonCode, string? EvidenceRef);

public interface IToolInputFilter
{
    // 返回新参数快照；管道随后 schema 校验并计算 hash。
    ValueTask<ToolInputTransform> TransformAsync(
        ToolInputContext context, CancellationToken cancellationToken);
}

public interface IToolAuthorizationFilter
{
    ValueTask<HookDecision> AuthorizeAsync(
        ToolCallContext context, CancellationToken cancellationToken);
}

public delegate ValueTask<ToolCallOutcome> ToolCallNext(
    CancellationToken cancellationToken);

public interface IToolCallMiddleware
{
    // 包裹整个调用；context 是请求级安全视图，next 不接收可替换 identity。
    ValueTask<ToolCallOutcome> InvokeAsync(
        ToolRequestContext context, ToolCallNext next,
        CancellationToken cancellationToken);
}

public interface IToolInvocationFilter
{
    // 仅包裹已经授权的终端执行，不能修改参数。
    ValueTask<ToolExecutionResult> InvokeAsync(
        AuthorizedToolContext context, ToolExecutionNext next,
        CancellationToken cancellationToken);
}

public interface IToolResultFilter
{
    ValueTask<ResultProjection> TransformAsync(
        ToolResultContext context, ResultProjection current,
        CancellationToken cancellationToken);
}

public interface IToolInvocationPipeline
{
    ValueTask<ToolCallOutcome> InvokeAsync(
        ToolCallRequest request, CancellationToken cancellationToken);
}
```

补充合同：IToolResourceFilter 的 AcquireAsync 返回可释放 lease/资源拒绝，框架将 Acquire/Dispose 编译成包裹后续执行的结构化 W，逆序释放；不是由 observer 事后补做清理。IToolExceptionMapper 接收安全异常分类 DTO 并返回 FailureInfo，IToolExecutorPort 接收授权后的不可变调用和不透明许可。许可只由系统审批/策略端口颁发，外部 DLL 不持有可复用 Core 权限凭据。

ToolRequestContext 是规范化前的安全请求视图；ToolCallContext 是规范化后的授权视图；AuthorizedToolContext 是提交前验证过的执行视图。不能把它们合并成所有阶段都可改写的 context.Items 字典。

### 4.2 Run 与 Session

```csharp
public interface IRunAdmissionFilter
{
    ValueTask<HookDecision> CheckAsync(
        RunAdmissionContext context, CancellationToken cancellationToken);
}

public interface IAgentRunMiddleware
{
    ValueTask<RunBodyResult> InvokeAsync(
        RunContext context, RunBodyNext next, CancellationToken cancellationToken);
}

public enum SettlementDisposition { Settle, Continue, Suspend, Fail }
public sealed record SettlementDecision(
    SettlementDisposition Disposition, string ReasonCode,
    ContinuationProposal? Continuation);

public interface IRunSettlementFilter
{
    ValueTask<SettlementDecision> EvaluateAsync(
        RunSettlementContext context, CancellationToken cancellationToken);
}

public interface IRunCommitPort
{
    // Existing canonical store adapter：按 executionId + expectedVersion 提交一次。
    ValueTask<RunCommitResult> TryCommitAsync(
        RunTerminalRecord record, long expectedVersion,
        CancellationToken cleanupToken);
}
```

IRunSettlementParticipant 在独立 cleanup deadline 下执行必要收口，使用 ledger 区分必需与可选工作。Session 的 Open/Suspend/Resume/Recover Filter 返回相应的 HookDecision；Open/Close ResourceParticipant 以 owner scope lease 管理资源。点位完整类型清单由目录驱动，不能每个组件再自建一套 OnStart/OnEnd。

### 4.3 事件与动作

```csharp
public sealed record AgentEventEnvelope<TPayload>(
    string EventId, string EventType, int SchemaVersion,
    DateTimeOffset OccurredUtc, long Sequence,
    InvocationIdentity Identity, long RegistryGeneration, TPayload Payload);

public interface IAgentEventObserver<TPayload>
{
    ValueTask ObserveAsync(AgentEventEnvelope<TPayload> fact,
        CancellationToken observerToken);
}

public interface IObserverSubscription : IAsyncDisposable
{
    string SubscriptionId { get; }
    ValueTask StopAcceptingAsync(CancellationToken cancellationToken);
}

public interface IActionHandler<TCommand, TResult>
{
    ValueTask<TResult> ExecuteAsync(
        ActionExecutionContext context, TCommand command,
        CancellationToken cancellationToken);
}

public interface IActionDispatchPort
{
    ValueTask<ActionResult> DispatchAsync(
        ActionEnvelope command, CancellationToken cancellationToken);
}
```

ActionEnvelope 字段见注册点目录；ActionResult 含 accepted/rejected、结果引用、EffectState、FailureInfo。Handler 唯一，G/W/O 可多个。生命周期 action 的 handler 固定为 coordinator；注册事务禁止替换系统 run.cancel 或 plugin.unload 等受保护动作。领域/插件 action 向 Agent 暴露时走工具入口；状态控制命令走受授权控制面，二者不能互相伪装。

内部事实发布端口仅提供给权威 coordinator，不作为插件 Core API。外部 observer 接收的是经权限过滤和脱敏后的信封，不能伪造 canonical 事实。

### 4.4 时间模块

ITimeTriggerStore 提供 ScheduleDefinition 读写、版本比较、触发 claim、checkpoint；ITriggerActionSubmitPort 接收 triggerId + 受约束 ActionEnvelope。TimeTriggerCoordinator 依赖这两个窄端口与 TimeProvider，不能持有 AgentExecutionService。

一次触发 claim 由 store 保证幂等；schedule 时区/DST、misfire、重叠策略显式建模。过期触发生成 skipped/missed 事实；触发处理超时不能生成第二个不同 commandId 重复动作。测试使用 FakeTimeProvider 或组件自己的可控时钟，不用真实 sleep 等待分钟级日历触发。

## 5. 集中的 C# 注册入口

```csharp
// 拟定：Source/PuddingHost/Composition/AgentExtensionsComposition.cs
public static class AgentExtensionsComposition
{
    public static IServiceCollection AddPuddingAgentExtensions(
        this IServiceCollection services, AgentExtensionOptions options)
    {
        var builder = new AgentExtensionsBuilder(services, options);

        builder
            .UseSystemLifecycle()       // 系统 coordinator / required 点位
            .UseRunPolicies()           // 准入、预算与收口策略
            .UseToolFirewall()          // 必需授权，不由 Agent 移除
            .UseToolResourcePolicies()  // 并发与配额
            .UseToolResultProtection()  // 必需脱敏及可选装饰
            .UseAgentTelemetry()        // 外层计时 + 只读观察者
            .UseContextAndLlmHooks()    // 各自声明自己的 point
            .UseReplyAndDeliveryHooks()
            .UsePuddingApplicationActions()
            .UsePuddingTimeTriggers()
            .UsePluginLifecycle();      // 主管，不在这里扫描执行 DLL

        builder.ValidateAndRegister(); // 验证候选全集，不能部分装配
        return services;
    }
}
```

该中央方法在原 Host 组合根调用一次，Console/DesktopChild 共用。它显示组件级注册关系；每个组件扩展方法内部声明所属 point 和具体项，管理查询可以展开整个有序表。不能靠调用几十个互不关联的 AddSingleton 隐藏扩展关系。

```csharp
// 初期位于 Host/Composition/Extensions，属于消费方侧适配。
public static class ToolFirewallExtensions
{
    public static AgentExtensionsBuilder UseToolFirewall(
        this AgentExtensionsBuilder builder)
    {
        builder.Services.AddScoped<LegacyFirewallFilter>();
        return builder.AddToolAuthorization<LegacyFirewallFilter>(
            id: "system.tool-firewall", order: 0,
            required: true, timeout: TimeSpan.FromSeconds(2));
    }
}

public static class AgentTelemetryExtensions
{
    public static AgentExtensionsBuilder UseAgentTelemetry(
        this AgentExtensionsBuilder builder)
    {
        builder.Services.AddSingleton<ToolLatencyMiddleware>();
        builder.Services.AddSingleton<ToolAuditObserver>();
        return builder
            .AddToolMiddleware<ToolLatencyMiddleware>("telemetry.tool-latency")
            .Subscribe<ToolCallFinished, ToolAuditObserver>(
                "telemetry.tool-finished", ObserverOptions.BestEffort)
            .AddAction<ExtensionInspectCommand, ExtensionInspectHandler>(
                "extensions.inspect", ActionOptions.ReadOnly);
    }
}
```

示例的 2 秒是可配置初始值，不是实测结论；人工审批不在该过滤器内部等用户，返回 RequireApproval 并创建可恢复审批记录。Owner 由 UseSystemLifecycle/注册会话分配，组件不能传字符串冒充系统 owner。Builder 不把 IServiceCollection/IServiceProvider 传给 Hook。

组件成为真正独立模块后，可提供专用 `.Composition` 适配项目的公开扩展方法，依赖 DI 抽象和自己模块；不能为了扩展方法把 Microsoft.Extensions.DependencyInjection/ASP.NET 引入 BCL Contracts。动态 Worker 注册使用相同描述事务和系统工厂，不修改 root DI 容器，也不运行来自 DLL 的任意宿主注册代码。

动作与时间也采用同一声明风格，例如时间组件的 UsePuddingTimeTriggers 内部声明：

```csharp
builder
    .AddAction<HealthSampleCommand, HealthSampleHandler>(
        "diagnostics.health.sample", ActionOptions.ReadOnly)
    .ScheduleAction(
        id: "system.health-sample",
        actionId: "diagnostics.health.sample",
        schedule: ScheduleOptions.Interval(
            TimeSpan.FromMinutes(5), MisfirePolicy.CoalesceLatest));
```

上述 schedule 示例必须配置有效 targetScope/actor 和权限，默认系统健康采样动作；不隐含唤醒任意 Agent。组件扩展方法接收明确配置选项，中央调用处能看到是否启用。Schedule 定义与 Action handler 同一事务验证，未知 target action 拒绝发布；运行时修改仍走受控命令。

## 6. 排序、事务和快照代码责任

| 类 | 单一职责 | 必须保证 |
|---|---|---|
| AgentExtensionPointCatalog | 点位到合同/阶段/允许 scope 的静态映射 | 未知 point 拒绝；定义不由插件修改 |
| RegistrationTransaction | 收集候选变更并验证全集 | required 缺失、冲突动作、类型不符、非法 trust 拒绝，原世代不变 |
| AgentExtensionRegistry | 发布不可变世代、提供索引 | 原子 cutover；单一 Sequence 分配；不执行 Hook |
| ExtensionSnapshot / SnapshotLease | 缓存排序后的描述和激活工厂 | 当前调用稳定；资源 owner 引用计数 |
| FilterChainCompiler | 按 stage 编译强类型链 | 只能连接匹配接口；阶段不可跨越；资源释放逆序 |
| HookCallBoundary | token、deadline、异常/失败政策 | 不把 observer 错误当 Deny；不吞必要审批/取消 |
| OnceNextGate | 捕获继续调用次数及任务 | next 第二次不进入下游；检测不 await next 的实现并等待已启动任务 |
| OwnerLeaseTracker | 停止新捕获与 drain | 真正退出前不 Dispose 工厂/实例；超时记录残留 |

排序 key：`(Stage, Order, ScopeRank, RegistrationSequence, RegistrationId)`；字符串比较 Ordinal。一次事务中的 Sequence 就是 builder 代码调用顺序。动态注册由事务分配后记录到注册诊断；不要由多线程注册竞态决定启动顺序。显式 Order 只在同 stage 有效。

PointId 使用目录中的稳定 ID（如 TL04），不是组件自行定义的别名。友好名称与阶段由 Catalog 决定。初版工具点位的 Builder 到 Catalog 映射如下；其他点位按目录同样定义，事件类型和 actionId 为另一索引，不与 pointId 混用。

| Builder API | PointId / 固定阶段 | 合同 |
|---|---|---|
| AddToolMiddleware | TL02 / WholeCall | IToolCallMiddleware |
| AddToolInputFilter | TL03 / PrepareInput | IToolInputFilter |
| AddToolAuthorization | TL04 / Authorize | IToolAuthorizationFilter |
| AddToolResourceFilter | TL05 / AcquireResource | IToolResourceFilter |
| AddToolInvocationFilter | TL06 / InvokeTerminal | IToolInvocationFilter |
| AddToolExceptionMapper | TL07 / MapExecutionFailure | IToolExceptionMapper |
| AddToolResultFilter | TL08 / ProjectResult | IToolResultFilter |
| Subscribe<ToolCallFinished, T> | TL09 的事实流 / Observe | IAgentEventObserver<ToolCallFinished> |

WholeCall 是固定外层 slot，不与内部 stage 简单拼成一个线性列表。ResourceParticipant 的结构化 Acquire/Dispose 与委托 W 使用不同编译适配器；执行器校验 interface 到 slot 的映射，不能用通用 string stage 任意插入。

排序示例：两项外层 W 的 Order 都为 0，先注册 A 再 B；执行 `A.before -> B.before -> 内层 -> B.after -> A.after`。Authorization G1/G2/G3 顺序执行，G2 Deny 时 G3 不执行，终端不执行，但 A/B 仍退出并记录拒绝 outcome。O1/O2 即使按顺序入队，也不保证 O1 处理完成后 O2 才处理。

Owner 注销事务：将 owner 标记 Draining → 从新快照剔除其非必需项/禁止新绑定 → 等活跃调用与 mailbox/job leases → dispose → Unloaded。替换 required 项必须同一事务有经验证的新项，否则拒绝；停机可以停止整个管道，不能以注销 required 放行。

## 7. 工具管道执行算法

```text
Invoke(request):
  validate trusted identity; allocate/claim callId; capture registry lease
  persist/enqueue ToolCallRequested according to event policy
  enter fixed failure boundary + sorted outer middleware
    resolve tool and binding; acquire binding reference
    enforce caller/size preflight
    bind input; run input transforms; revalidate; freeze arguments hash
    await each authorization guard; first non-Allow short-circuits
    acquire sorted resource filters; record queue/lease spans
    validate permission epoch, binding generation, argument/approval hash
    enter sorted invocation filters with protected next
      invoke terminal exactly once for this attempt; capture raw result
    map tool execution fault; freeze raw result and EffectState
    run sorted result projection filters; validate safety and size
  finally:
    wait any already-started next task / actual worker termination policy
    release entered resources in reverse order using cleanup deadline
    submit one canonical call terminal; publish ToolCallFinished
    release snapshot/binding leases; exit outer scopes
  return typed outcome
```

实现中 outer middleware 是嵌套 delegate；终态 commit 放在固定框架边界中，不能由某个扩展决定是否执行。计时中间件返回之前应看到最终 outcome；框架检查 middleware 返回值与封存原始结果是否一致。资源清理失败是独立清理字段/基础设施故障，不能用 finally 的 throw 覆盖原始工具错误。

OnceNextGate 不只计数：记录下游 Task，发现 extension 返回时下游仍在运行要受控等待/取消并保留租约；不能提前发布 finished 或释放被下游使用的资源。next 零次仅在允许短路的 W 合同中成立，否则 HookContractViolation。最多一次不是跨重启 exactly-once 副作用保证，远端幂等另由工具合同负责。

### 7.1 防火墙适配示例

LegacyFirewallFilter 位于消费方，依赖现有 IAgentFirewall，将 ToolCallContext 转为 FirewallContext，再将现有 FirewallDecision 映射到 HookDecision。这个适配器可以认识旧实现；新 Pipeline 不能认识 AgentFirewall。迁移时去掉执行器的可选 fallback/new，并要求装配验证必需策略存在。

依次抽取八道门禁，每次先在独立 Policies 用假端口验证既有决定向量，再用中央 UseToolFirewall 注册。不能在第一次接入时顺便改变审批策略或把全部判断挪进 Kernel。

### 7.2 延迟统计示例

```csharp
public sealed class ToolLatencyMiddleware(
    TimeProvider clock, IToolMetricsSink metrics) : IToolCallMiddleware
{
    public async ValueTask<ToolCallOutcome> InvokeAsync(
        ToolRequestContext context, ToolCallNext next, CancellationToken ct)
    {
        var started = clock.GetTimestamp();
        ToolCallOutcome? outcome = null;
        try
        {
            outcome = await next(ct);
            return outcome;
        }
        finally
        {
            // TryRecord：有界、非抛错、非阻塞；失败返回 false 并增加 dropped 计数。
            metrics.TryRecord(new ToolDurationSample(
                context.Identity.ExecutionId, context.Identity.CallId!,
                clock.GetElapsedTime(started), outcome?.Status));
        }
    }
}
```

IToolMetricsSink 合同保证 TryRecord 不抛异常、不执行数据库写入；存储在其消费者内完成。框架仍隔离不遵守合同的可选 telemetry 组件，并保留 next 已产生结果，不能因统计失败重试工具。异常/进程中断没有完整 outcome 时标注 missing/unknown，不伪造成功。

## 8. Run 状态机与终态收口

RunCoordinator 只依赖执行 body、管道、准入端口、预算、IRunCommitPort、事件 sink 和 owner ledger。现有主类里的记忆写入、审批、日志/渠道投影通过所属端口处理，不能全部作为构造参数搬进新类。

```text
Execute(command):
  claim command identity and expected version
  admission guards + execution lease
  try:
    start run middleware/body segment
    rounds call context/LLM/tool/reply ports
    classify body ending
    before_settle guards propose typed outcome/continuation
    if continuation accepted and original budgets allow: next segment
  catch: classify failure without losing effect/terminal records
  finally:
    stop new run-owned work; bounded drain of necessary work
    finalizer persists required artifacts and terminal with first-writer-wins
    release execution resources; commit settled checkpoint/outbox
    publish RunSettled only from committed settled checkpoint
```

RunFinalizer 用可恢复的 finalizing/checkpoint 协议处理“终态记录已写，资源未释放”的进程中断；不能要求数据库事务持有期间等待 Worker 退出。逻辑 terminal record 与 settled checkpoint 明确区分；状态投影不把前者提前展示成最终收口。owner lease 释放失败会阻止 settled，诊断记录 settlement.failed，恢复主管 reconcile。

必要工作账本包括受要求的子代理 join、关键写入、必须清理资源；非必要 Embedding/统计另建 job，挂在后台 owner。Closing/Draining 拒绝 continuation；取消后的 cleanup token 来自 finalizer 的独立 deadline。外部控制器判断 Desktop/Core 进程生命周期，不用 RuntimeStopped 内部事件代替退出验收。

## 9. 观察、动作与时间执行设施

ObserverDispatcher：从快照获得订阅描述，每项独立有界 mailbox，按分区保序；执行 ObserveAsync 的异常/超时生成安全诊断和健康状态。过载策略显式 DropNewest/Coalesce/持久消费，不能静默无界 Task.Run。必需审计使用 canonical outbox，不作为允许丢失的普通 observer。

ActionDispatchPipeline：认证 identity → 版本/幂等 claim → 解析唯一 handler → 系统/领域准入过滤器 → 所属资源/执行管道 → action result commit → finished fact。返回交付结果不等于外部 effect 成功，保留 effectState。冲突命令/actor/权限不复用旧幂等结果；同键但不同 payload 返回 IdempotencyConflict。

TimeTriggerCoordinator：读取日历计划 → 原子 claim trigger → 持久提交命令 → checkpoint；worker 消费命令时再次检查权限/owner 有效性。停机先停止新 trigger，再 drain；唤醒/重启按 misfire 策略补发。Run deadline 是单调预算控制，不由日历 scheduler 周期扫描实现。

中央 registry 只管理定义/索引；上述三个设施各有窄接口。管理 API 从 IExtensionDiagnosticsPort 查询，不直接探内部字典、日志或 DbContext。诊断记录失败走专用 sink，防止观察自己的错误产生无限事件链。

## 10. 现有代码逐项迁移

| 当前位置 | 目标与接入方式 | 不得丢失的行为 |
|---|---|---|
| AgentExecutionService + Buffered/Streaming | LegacyRunBodyAdapter 先接入共同工具/生命周期骨架，再逐步抽 Context、LLM、Reply | 预算、取消、WAIT、子代理 archive、canonical terminal 围栏 |
| IAgentLoopHook / FireHooksAsync | LegacyLoopObserverAdapter 仅迁移通知；安全功能单独注册 G/T/W | 不假装无返回值 Hook 是权限过滤器；统一两种执行覆盖 |
| AgentFirewall / PuddingToolExecutionService | LegacyFirewallFilter → 独立 Policies；执行器变终端端口 | 八类门禁、RequireApproval/Defer、沙箱隔离 |
| HookPublisher / InternalEventBus | 兼容事实桥接，逐消费者迁移 mailbox/outbox | 原来持久队列的幂等和恢复，不降级到最佳努力 |
| EmbeddingGenerationHook | 对 RunSettled 等事实提交独立 Job，读记忆查询端口 | 明确 workspace、owner、并发和恢复 |
| ContextPipeline / Compaction | contributor/transform/admission；数据读取迁仓储端口 | canonical 用户围栏、候选版本、硬保护与现有策略 |
| SessionStateManager / RuntimeControlService | 既有存储与控制适配；协调器成为状态权威 | 已有终态/序号/控制许可，不新开第二份状态表 |
| AgentDiagnosticsTool 的日志解析 | IExtensionDiagnosticsPort + 专用 compaction 查询 | limit 在数据源侧生效、脱敏及证据引用 |
| Host 渠道投影/应用 DB 操作 | 领域模块 + repository + action/delivery ports | persist-before-notify、投递幂等 |
| 插件管理与外部 DLL | 受控注册事务、Worker bridge、owner revoke/drain | 元数据扫描不执行 DLL；外部故障不崩溃 Core |

不用“让所有类多注入一个 IHookPublisher”作为完成标准；关键是移除具体功能对核心的侵入和旧调用旁路。初版适配器留在消费方，不能让新 Contracts/Kernel 引用 Runtime。

## 11. 类注释模板与文档门禁

```csharp
/// <summary>
/// ToolInvocationPipeline：按固定阶段执行一次工具调用。
/// description: 负责过滤器调度与终态收口，不实现工具、权限策略或存储。
/// </summary>
/// <remarks>
/// categories: agent, invocation, infrastructure
/// related_files: Execution/OnceNextGate.cs; Registry/ExtensionSnapshot.cs
/// related_docs: Docs/12_features/Agent生命周期与Hook管道代码设计-2026-10-09.md
/// invariants: identity 固定；授权后参数不可变；next 至多一次；终态有来源。
/// lifetime: 共享编排器无调用可变字段，调用数据位于独立 scope。
/// failure_policy: 安全策略失败拒绝；只读观察失败隔离。
/// </remarks>
```

related_files 相对所属项目，related_docs 相对仓库根；真实创建文件时验证目标存在。字段可用于 Roslyn analyzer/文档生成，但先保留 XML 可读性。Contracts 类型写载荷与不变量；实现类写依赖的端口、资源 lifetime 和失败规则。上例是目标布局内的关系，当前文档不将尚未创建的文件当有效站内链接。

新组件代码门禁：可见类型都有有效 summary/description/categories/关联入口；关联允许合理空列表；禁止无意义复制说明。改动旧类型时补齐，批量补齐另立任务，不把注释覆盖率当架构清洁度。

## 12. 测试矩阵与施工任务

| 套件 | 必须验证 |
|---|---|
| ContractsBoundaryTests | 无 Web/EF/Runtime/Host/DI 引用；类型 payload 没有可写内部对象；作者独立示例编译 |
| RegistrationTests | point/接口匹配、重复 ID/动作拒绝、required 缺失、非法 owner/scope/trust、原子事务回退 |
| OrderingTests | stage 固定；Order/Scope/Sequence 正序；middleware 逆序；观察者不依赖完成顺序 |
| ToolPipelineTests | deny 不执行；多个 allow 后 deny；approval/defer 保真；输入变更校验与批准 hash；权限撤销 |
| OnceNextTests | 第二次 next 不产生第二次副作用；未 await next 不提前释放；零次按合同判定 |
| HookFaultTests | guard 异常/超时 fail-closed；observer 异常隔离；必需投影隐藏；可选计时失败保留结果 |
| EffectTests | Worker 断开/超时 Unknown；投影/持久化失败不重跑工具；幂等冲突拒绝 |
| LifecycleTests | 合法/非法转换、取消、Waiting/恢复、continuation 预算、所有终态出口与 checkpoint 恢复 |
| OwnershipTests | 快照替换不改变在途调用；卸载停止捕获、真实 drain、超时隔离与残留查询 |
| ObserverTests | 有界队列/丢失计数、保序、unsubscribe、幂等重放、错误不递归 |
| ActionTests | handler 唯一、系统动作不可替换、Agent/app 动作路由、授权/版本/幂等/因果环限制 |
| TimeTriggerTests | once/interval/日历、DST/休眠/重启/回拨、misfire、重叠抑制、触发幂等与撤权 |
| BoundaryMutationTests | 非法反向项目/包引用能取红；S1–S4 不加载宿主/EF/ASP.NET |
| HostCompositionTests（S5） | 中央入口完整、必需组件验证、Streaming/Buffered/委派/Worker 工具均经过唯一管道 |
| ExternalProbe / ProductSmoke | 真实 DLL 故障/卸载、管理动作与诊断；新构建加载明确；进程生命周期由外部控制器验收 |

施工顺序：先评审点位目录和 C0 合同 → 独立 Contracts → Pipeline 注册与故障边界 → Kernel 状态/收口 → Policies/TimeTriggers 独立测试 → S5 中央组合入口与适配 → 删除旧旁路 → Worker/UI 接入。每个组件先独立构建/测试/边界验证，再登记解决方案与 DI；不能为了提前展示 UI 将未验证内核接进宿主。

验收资产应固化输入/预期决定/真实副作用计数/终态账本/资源释放证据，不写只镜像实现的测试。构建测试输出使用 temp/build 与 temp/test-out。本次文档门禁只验证 Front Matter、链接、源码快照和点位/设计一致性，没有宣称这些未来测试已经通过。
