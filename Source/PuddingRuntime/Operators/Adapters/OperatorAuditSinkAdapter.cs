using Microsoft.Extensions.Logging;
using PuddingCode.Operators;
using PuddingCode.Tools;

namespace PuddingRuntime.Operators.Adapters;

/// <summary>
/// 算子审计旁挂的<b>生产实现</b>（S2b 交付物 2）：把 S1a 接缝 <see cref="IOperatorAuditSink"/>
/// 接到<b>既有</b>审批审计存储 <see cref="IToolApprovalAuditStore"/> 上。
/// <para>
/// <b>为什么需要它</b>：S1a 定义了端口却<b>没有任何生产实现</b>——接缝因此是惰性的（定义了端口但没人实现
/// ⇒ 永远不会被调用）。本适配器把「算子判定留痕」接进既有 append-only 审计流，避免另起一套存储。
/// </para>
/// <para>
/// <b>不可回退的既有契约「裁决先于留痕」</b>：审计写入<b>不</b>是裁决的同步必经环节——写入失败
/// <b>不改变</b>已定裁决、<b>只记 Warning</b>、<b>绝不上抛</b>。本类严格照此实现：整段写入包在
/// try/catch 内，catch 里只记 Warning + 递增可探查计数，绝不重新抛出。
/// </para>
/// <para>
/// <b>哪一层是权威兜底（§3.3，禁止后人误删）：</b>
/// <list type="number">
/// <item><b>权威兜底层 = <c>OperatorBase</c></b>（S1a 基类）。它包住<b>任意</b>端口实现：只要有实现违反契约
/// 把异常抛出来，基类就会捕获并保证「裁决不受影响」。这层是<b>契约被违反时的唯一防线</b>，不可删。</item>
/// <item><b>本适配器 = 纵深防御的第二层</b>，且是<b>唯一记日志层</b>：本层在自己的协作里就把异常吞掉并记
/// Warning，因此对<b>本适配器路径</b>而言不存在「两层各记一条」的重复日志——基类的 catch 只会在异常真的
/// 冒泡时触发（即实现没按契约吞掉，例如第三方实现）。两层同时存在不是冗余：一层保证<b>裁决</b>安全，
/// 一层保证<b>故障可见</b>。</item>
/// </list>
/// </para>
/// <para>
/// <b>字段映射（如实说明，不伪造）</b>：既有审计事件没有「场景键」字段，场景键因此以 <c>key=value</c> 前缀
/// 写进 <c>Reason</c>（与管线覆盖审计同一约定），原始理由原样保留在分隔符之后。<c>EventId</c> 取判定的
/// 确定性 id（同一判定重复写入不会产生两条语义不同的记录），<c>ClassifierId</c> 承载算子稳定标识。
/// 事件种类的既有枚举里已有「分类器产出一次判定」成员（S1 追加），直接复用——<b>不新增枚举成员、不改
/// PuddingCore 一行</b>。<c>Effect</c> 保持既有默认值：本记录不表达规则效果（allow/deny），不臆造。
/// </para>
/// <para>
/// <b>同步端口 ↔ 异步存储的桥</b>：接缝是同步 <c>void Write</c>，既有存储是 <c>Task SaveAsync</c>。
/// 这里用 <see cref="Task.Run{TResult}(Func{TResult})"/> 把写入放到线程池执行后再同步等待：
/// ① 必须<b>等待</b>，否则失败不可观测（「只记 Warning」就变成「什么都不记」）；
/// ② 必须<b>离开调用方上下文</b>，否则在带同步上下文（如桌面宿主）的线程上同步等待异步续体会自锁。
/// 该写入只在判定结束时发生（非热路径），代价可接受。
/// </para>
/// </summary>
public sealed class OperatorAuditSinkAdapter : IOperatorAuditSink
{
    private readonly IToolApprovalAuditStore? _store;
    private readonly ILogger? _logger;

    private int _swallowedFailures;
    private string? _lastSwallowedFailure;
    private int _unwiredWarningLogged;

    /// <summary>构造适配器。</summary>
    /// <param name="store">既有审计存储；null 表示宿主未接线（此时写入被如实记为「未接线」并可探查）。</param>
    /// <param name="logger">日志；null 表示不记日志（此时仍可通过 <see cref="SwallowedFailureCount"/> 探查）。</param>
    public OperatorAuditSinkAdapter(IToolApprovalAuditStore? store, ILogger? logger = null)
    {
        _store = store;
        _logger = logger;
    }

    /// <summary>被吞掉的（未上抛的）失败次数：写入失败 + 未接线写入（只读，供探查）。</summary>
    public int SwallowedFailureCount => Volatile.Read(ref _swallowedFailures);

    /// <summary>最近一次被吞掉的失败摘要（<c>异常类型: 消息</c> 或「存储未接线」）；从未发生为 null。</summary>
    public string? LastSwallowedFailure => _lastSwallowedFailure;

    /// <inheritdoc />
    public void Write(OperatorAuditRecord record)
    {
        if (record is null)
        {
            return;
        }

        var store = _store;
        if (store is null)
        {
            // 未接线 ⇒ 写入被丢弃，但绝不静默：首次记 Warning（只记一次，避免每次判定刷屏），
            // 并把「丢弃过」留在可探查面上。
            Swallow(null, "审计存储未接线：写入被丢弃（裁决不受影响）。", record);
            return;
        }

        try
        {
            var auditEvent = ToAuditEvent(record);

            // 同步等待异步存储：见类注释「同步端口 ↔ 异步存储的桥」。
            Task.Run(async () => await store.SaveAsync(auditEvent).ConfigureAwait(false)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // 裁决先于留痕：写入失败不改变已定裁决、只记 Warning、不上抛（权威兜底在基类，见类注释）。
            Swallow(ex, null, record);
        }
    }

    /// <summary>
    /// 既有审计记录的映射（纯映射）。<c>EventId</c> 用判定的确定性 id：重复写入不会产生两条语义不同的记录；
    /// 场景键与原因码以 <c>key=value</c> 前缀落在 <c>Reason</c> 里（既有事件无场景字段），原始理由保留在其后。
    /// </summary>
    private static ToolApprovalAuditEvent ToAuditEvent(OperatorAuditRecord record) => new()
    {
        EventId = record.JudgementId,
        EventType = ToolApprovalAuditEventType.ClassifierInvoked,
        ClassifierId = record.OperatorId,
        Reason = string.Concat(
            "operator=", record.OperatorId,
            " scene=", record.SceneKey,
            " reasonCode=", record.ReasonCode ?? "none",
            " cached=", record.Cached ? "true" : "false",
            " | ", record.Reason),
        CreatedAtUtc = record.CreatedAtUtc,
    };

    private void Swallow(Exception? ex, string? note, OperatorAuditRecord record)
    {
        Interlocked.Increment(ref _swallowedFailures);

        var summary = ex is null ? note! : $"{ex.GetType().Name}: {ex.Message}";
        _lastSwallowedFailure = summary;

        if (ex is not null)
        {
            _logger?.LogWarning(
                ex,
                "算子审计旁挂写入失败（裁决先于留痕，不影响已定裁决）：judgement={JudgementId} operator={OperatorId}",
                record.JudgementId,
                record.OperatorId);
            return;
        }

        if (Interlocked.Exchange(ref _unwiredWarningLogged, 1) == 0)
        {
            _logger?.LogWarning(
                "算子审计旁挂未接线（未注册审计存储）：审计写入被丢弃，裁决不受影响；请检查宿主 DI 组装。judgement={JudgementId}",
                record.JudgementId);
        }
    }
}
