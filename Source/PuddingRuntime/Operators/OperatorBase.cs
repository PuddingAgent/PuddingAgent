using System.Globalization;
using Microsoft.Extensions.Logging;
using PuddingCode.Operators;

namespace PuddingRuntime.Operators;

/// <summary>
/// 算子基础设施基类（基础设施第 1 层）。
/// <para>
/// 横切职责<b>全部非虚</b>，由本类统一承担，子类不得覆盖：
/// <list type="number">
/// <item><b>输入指纹与缓存</b>：键 = 算子 | 算子版本 | 指令版本 | 输入指纹；命中即置 <c>Cached=true</c> 并<b>短路模型调用</b>；</item>
/// <item><b>独立超时与取消</b>：独立 <see cref="CancellationTokenSource"/>；取消 / 超时<b>不冒泡异常</b>，转降级结论 + 稳定原因码；</item>
/// <item><b>异常兜底</b>：<see cref="ClassifyCoreAsync"/> 抛出的任何异常 ⇒ 降级结论（对齐既有不变式：禁止向调用方冒泡异常）；</item>
/// <item><b>模型调用封装</b>：经 <see cref="IClassifierModel"/>，含重试与超时（S1a 只做接缝与最小实现，不接真实供应商）；</item>
/// <item><b>健康上报</b>：成功 / 失败 / 缓存标记（接缝，见 <see cref="IOperatorHealthObserver"/>）；</item>
/// <item><b>审计旁挂</b>：旁挂、失败不影响裁决、失败必留 Warning。</item>
/// </list>
/// </para>
/// <para>
/// <b>不可回退的既有契约</b>：审计<b>不得</b>成为同步必经环节。既有语义是「<b>裁决先于留痕</b>」——
/// 审计写入失败不改变已定裁决，只记 Warning；把审计提为同步必经步骤即回退该契约。
/// </para>
/// <para>
/// <b>与既有工具调用裁决契约的关系</b>：本基类不产生既有裁决记录形状的耦合——两者并存、互不引用
/// （适配属后续切片）。
/// </para>
/// </summary>
/// <typeparam name="TCtx">场景输入上下文类型（实现 <see cref="IOperatorContext"/>）。</typeparam>
/// <typeparam name="TResult">投影结果类型（<see cref="ScoreResult"/> / <see cref="JudgeResult"/> / <see cref="ClassificationResult"/>）。</typeparam>
public abstract class OperatorBase<TCtx, TResult>
    where TCtx : class, IOperatorContext
{
    private readonly OperatorEnvironment _environment;
    private readonly Func<OperatorScope, OperatorDegradation, TResult> _degradedResultFactory;

    /// <summary>构造算子基类。</summary>
    /// <param name="environment">算子运行环境。</param>
    /// <param name="degradedResultFactory">
    /// 降级结论工厂：由投影基类提供。放在构造参数而<b>不</b>做成第 7 个抽象声明，
    /// 以免破坏「场景层恰好 1 层、唯一可变点是 <see cref="ClassifyCoreAsync"/>」的分层约束。
    /// </param>
    /// <exception cref="ArgumentNullException">参数为 null。</exception>
    /// <exception cref="ArgumentOutOfRangeException">超时不是正时长。</exception>
    protected OperatorBase(OperatorEnvironment environment, Func<OperatorScope, OperatorDegradation, TResult> degradedResultFactory)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(degradedResultFactory);

        if (environment.Timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(environment), "OperatorEnvironment.Timeout 必须为正时长。");
        }

        _environment = environment;
        _degradedResultFactory = degradedResultFactory;
    }

    /// <summary>
    /// 算子稳定标识（取实现类型全名）。<b>不可覆盖</b>：子类不得改写，否则缓存键与审计溯源可被绕过。
    /// <para>
    /// 注：本属性同时是对外原语端口 <see cref="IOperator.OperatorId"/> 的隐式实现，编译器会把它发射为
    /// <c>virtual final</c>（反射可见 <c>IsVirtual=true, IsFinal=true</c>）——<c>IsFinal</c> 使其<b>无法被派生类覆盖</b>，
    /// 因此「横切职责非虚」的不变式仍然成立。
    /// </para>
    /// </summary>
    public string OperatorId => GetType().FullName ?? GetType().Name;

    /// <summary>算子版本（取承载本基类的程序集版本）。<b>非虚</b>。</summary>
    public string? OperatorVersion => typeof(OperatorBase<TCtx, TResult>).Assembly.GetName().Version?.ToString();

    // ── 6 项抽象声明：编译期强制，缺一不编译；**不得提供默认值** ──

    /// <summary>1. 场景键。</summary>
    protected abstract string SceneKey { get; }

    /// <summary>2. 指令（文本 + 版本 + 问题集）。</summary>
    protected abstract OperatorInstruction Instruction { get; }

    /// <summary>3. 输出形状（标签集 / 分数刻度）。</summary>
    protected abstract OperatorOutputShape OutputShape { get; }

    /// <summary>
    /// 4. 阈值策略。<b>必须来自 provider，不得硬编码常量</b>；
    /// 推荐实现：<c>protected override ThresholdPolicy? Threshold =&gt; ResolveThresholdFromProvider();</c>
    /// </summary>
    protected abstract ThresholdPolicy? Threshold { get; }

    /// <summary>5. 输入投影：canonical 上下文 → 结构化片段（必须确定性）。</summary>
    protected abstract string ProjectInput(TCtx context);

    /// <summary>6. 结果投影：模型判断 → 结果（必须确定性、纯函数）。</summary>
    protected abstract TResult Project(ModelJudgement judgement, TCtx context, OperatorScope scope);

    /// <summary>
    /// <b>唯一可变点</b>：场景算子内核。其余成员一律非虚。
    /// </summary>
    protected abstract Task<TResult> ClassifyCoreAsync(TCtx context, OperatorScope scope, CancellationToken ct);

    // ── 横切流程（全部非虚）──

    /// <summary>
    /// 阈值的推荐取法（非虚）：从注入的 provider 解析，避免场景侧硬编码常量。
    /// </summary>
    protected ThresholdPolicy? ResolveThresholdFromProvider() => _environment.ThresholdPolicies?.Resolve(SceneKey);

    /// <summary>
    /// 统一入口（非虚）：上下文缺失或类型不匹配一律转降级结论，绝不抛异常。
    /// </summary>
    /// <param name="context">场景输入上下文（由原语端口的调用方传入）。</param>
    /// <param name="ct">取消令牌。</param>
    protected Task<TResult> RunAsync(IOperatorContext context, CancellationToken ct)
    {
        if (context is null)
        {
            return Task.FromResult(BuildDegradedResult(null, OperatorReasonCodes.ContextMismatch, "输入上下文为空：按降级契约返回结论。"));
        }

        if (context is TCtx typed)
        {
            return RunAsync(typed, ct);
        }

        return Task.FromResult(BuildDegradedResult(
            context,
            OperatorReasonCodes.ContextMismatch,
            $"输入上下文类型 {context.GetType().Name} 与算子期望的 {typeof(TCtx).Name} 不匹配：按降级契约返回结论。"));
    }

    /// <summary>
    /// 统一入口（非虚）：跑完整横切流程（缓存 → 模型 → 内核 → 健康 → 审计旁挂）。
    /// </summary>
    /// <param name="context">场景输入上下文。</param>
    /// <param name="ct">取消令牌。</param>
    protected Task<TResult> RunAsync(TCtx context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        return RunCoreAsync(context, ct);
    }

    /// <summary>
    /// 构造降级结论（非虚）：供投影基类在入口前置失败（如上下文类型不匹配）时复用。
    /// </summary>
    /// <param name="context">可用的输入上下文；无则 null。</param>
    /// <param name="reasonCode">稳定原因码。</param>
    /// <param name="reason">人类可读理由。</param>
    protected TResult BuildDegradedResult(IOperatorContext? context, string reasonCode, string reason)
    {
        var clock = _environment.Clock ?? TimeProvider.System;
        var scope = CreateScope(
            context,
            sceneKey: context?.SceneKey ?? string.Empty,
            instructionVersion: 0,
            renderedInput: string.Empty,
            threshold: null,
            clock,
            startedAt: clock.GetUtcNow(),
            ct: CancellationToken.None);

        return Finish(scope, new OperatorDegradation(reasonCode, reason));
    }

    private async Task<TResult> RunCoreAsync(TCtx context, CancellationToken ct)
    {
        var clock = _environment.Clock ?? TimeProvider.System;
        var scope = CreateScope(
            context,
            sceneKey: string.Empty,
            instructionVersion: 0,
            renderedInput: string.Empty,
            threshold: null,
            clock,
            startedAt: clock.GetUtcNow(),
            ct);

        if (ct.IsCancellationRequested)
        {
            return Finish(scope, new OperatorDegradation(OperatorReasonCodes.Cancelled, "调用方在算子启动前取消：按降级契约返回结论，不冒泡异常。"));
        }

        using var timeoutCts = new CancellationTokenSource(_environment.Timeout, clock);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var token = linkedCts.Token;

        try
        {
            var instruction = Instruction;
            var policy = Threshold;

            if (policy is not null && !policy.IsValid)
            {
                scope = scope with { SceneKey = SceneKey, InstructionVersion = instruction.Version };
                return Finish(scope, new OperatorDegradation(
                    OperatorReasonCodes.InvalidThreshold,
                    $"阈值策略配置非法（{policy.PolicyId} v{policy.Version}）：YesAtOrAbove({policy.YesAtOrAbove}) "
                    + $"必须严格大于 NoAtOrBelow({policy.NoAtOrBelow})。拒绝裁决，不得静默产生全 Yes / 全 No / 全 Abstain。"));
            }

            var renderedInput = ProjectInput(context) ?? string.Empty;
            scope = scope with
            {
                SceneKey = SceneKey,
                InstructionVersion = instruction.Version,
                RenderedInput = renderedInput,
                Threshold = policy?.ToApplied(),
            };

            var resolution = await ResolveJudgementAsync(instruction, scope, token).ConfigureAwait(false);
            scope = scope with { ModelJudgement = resolution.Judgement, Cached = resolution.Cached };
            if (resolution.Failure is { } modelFailure)
            {
                return Finish(scope, modelFailure);
            }

            var result = await ClassifyCoreAsync(context, scope, token).ConfigureAwait(false);
            if (result is null)
            {
                return Finish(scope, new OperatorDegradation(OperatorReasonCodes.CoreFailure, "算子内核返回 null：按降级契约返回结论。"));
            }

            Complete(scope, succeeded: true, reasonCode: null, reason: "判定完成。");
            return result;
        }
        catch (OperationCanceledException)
        {
            var cancelled = ct.IsCancellationRequested;
            return Finish(scope, cancelled
                ? new OperatorDegradation(OperatorReasonCodes.Cancelled, "调用方取消：按降级契约返回结论，不冒泡异常。")
                : new OperatorDegradation(
                    OperatorReasonCodes.Timeout,
                    $"算子超时（{_environment.Timeout}，独立超时链路）：按降级契约返回结论，不冒泡异常。"));
        }
        catch (Exception ex)
        {
            return Finish(scope, new OperatorDegradation(
                OperatorReasonCodes.CoreFailure,
                $"算子内核异常（{ex.GetType().Name}: {ex.Message}）：按降级契约返回结论，不冒泡异常。"));
        }
    }

    private async Task<JudgementResolution> ResolveJudgementAsync(OperatorInstruction instruction, OperatorScope scope, CancellationToken token)
    {
        var model = _environment.Model;
        if (model is null || instruction.Questions.Count == 0)
        {
            return new JudgementResolution(null, false, null);
        }

        var cache = _environment.Cache;
        var cacheKey = BuildCacheKey(scope);
        if (cache is not null && cache.TryGet(cacheKey, out var cached) && cached is not null)
        {
            return new JudgementResolution(cached, true, null);
        }

        var request = new ModelJudgementRequest
        {
            SceneKey = scope.SceneKey,
            Instruction = instruction.Text,
            InstructionVersion = instruction.Version,
            Questions = instruction.Questions,
            RenderedInput = scope.RenderedInput,
            InputDigest = scope.InputDigest,
        };

        var attempts = Math.Max(1, _environment.ModelRetryLimit + 1);
        Exception? lastFailure = null;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var judgement = await model.JudgeAsync(request, token).ConfigureAwait(false);
                if (judgement is null)
                {
                    throw new InvalidOperationException("模型端口返回 null。");
                }

                cache?.Set(cacheKey, judgement);
                return new JudgementResolution(judgement, false, null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastFailure = ex;
            }
        }

        return new JudgementResolution(
            null,
            false,
            new OperatorDegradation(
                OperatorReasonCodes.ModelUnavailable,
                $"模型不可用（{attempts} 次尝试后仍失败：{lastFailure?.GetType().Name}: {lastFailure?.Message}）：按降级契约返回结论。"));
    }

    private TResult Finish(OperatorScope scope, OperatorDegradation degradation)
    {
        var result = _degradedResultFactory(scope, degradation);
        Complete(scope, succeeded: false, degradation.ReasonCode, degradation.Reason);
        return result;
    }

    private void Complete(OperatorScope scope, bool succeeded, string? reasonCode, string reason)
    {
        ReportHealth(scope, succeeded, reasonCode);
        ReportAudit(scope, reasonCode, reason);
    }

    private void ReportHealth(OperatorScope scope, bool succeeded, string? reasonCode)
    {
        var observer = _environment.Health;
        if (observer is null)
        {
            return;
        }

        try
        {
            observer.Report(new OperatorHealthSample
            {
                OperatorId = scope.OperatorId,
                SceneKey = scope.SceneKey,
                Succeeded = succeeded,
                Cached = scope.Cached,
                ReasonCode = reasonCode,
                LatencyMs = scope.ElapsedMs,
                ObservedAtUtc = scope.Clock.GetUtcNow(),
            });
        }
        catch (Exception ex)
        {
            _environment.Logger?.LogWarning(ex, "算子健康上报失败（旁挂，不影响裁决）：{OperatorId}/{SceneKey}", scope.OperatorId, scope.SceneKey);
        }
    }

    private void ReportAudit(OperatorScope scope, string? reasonCode, string reason)
    {
        var sink = _environment.Audit;
        if (sink is null)
        {
            return;
        }

        try
        {
            sink.Write(new OperatorAuditRecord
            {
                OperatorId = scope.OperatorId,
                SceneKey = scope.SceneKey,
                JudgementId = scope.JudgementId,
                Reason = reason,
                ReasonCode = reasonCode,
                Cached = scope.Cached,
                CreatedAtUtc = scope.Clock.GetUtcNow(),
            });
        }
        catch (Exception ex)
        {
            _environment.Logger?.LogWarning(
                ex,
                "算子审计旁挂写入失败（裁决先于留痕，不影响已定裁决）：{JudgementId}",
                scope.JudgementId);
        }
    }

    private string BuildCacheKey(OperatorScope scope) => string.Join(
        '|',
        OperatorId,
        OperatorVersion ?? string.Empty,
        scope.InstructionVersion.ToString(CultureInfo.InvariantCulture),
        scope.InputDigest);

    private OperatorScope CreateScope(
        IOperatorContext? context,
        string sceneKey,
        int instructionVersion,
        string renderedInput,
        AppliedThreshold? threshold,
        TimeProvider clock,
        DateTimeOffset startedAt,
        CancellationToken ct) => new()
        {
            OperatorId = OperatorId,
            OperatorVersion = OperatorVersion,
            SceneKey = sceneKey,
            InputDigest = context?.InputDigest ?? string.Empty,
            InstructionVersion = instructionVersion,
            RenderedInput = renderedInput,
            Identity = (context as IOperatorIdentityContext)?.Identity,
            Threshold = threshold,
            StartedAtUtc = startedAt,
            Clock = clock,
            Cancellation = ct,
        };

    /// <summary>模型判断解析结果：判断 / 是否命中缓存 / 失败原因（三者互斥语义见字段注释）。</summary>
    private sealed record JudgementResolution(ModelJudgement? Judgement, bool Cached, OperatorDegradation? Failure);
}
