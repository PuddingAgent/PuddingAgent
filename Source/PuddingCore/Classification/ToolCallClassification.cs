namespace PuddingCode.Classification;

/// <summary>
/// 安全分类器对单次工具调用请求的裁决结论（四选一 + 未知兜底）。
/// <para>
/// 本枚举是分类器抽象层的核心契约：系统黑白名单未命中时，由 <see cref="IToolCallClassifier"/>
/// 给出四选一结论；<see cref="Unknown"/> 表示「未产生有效裁决」（输入不足、规则类实现未命中等），
/// 调用方必须按降级契约处理，<b>不得</b>把 <see cref="Unknown"/> 当作放行。
/// </para>
/// </summary>
/// <remarks>
/// 序列化兼容：<see cref="Unknown"/> 必须保持数值 0（字段缺省反序列化值）；
/// 新增成员只允许追加在枚举末尾，不得插入或重排（避免改变既有序列化数值）。
/// </remarks>
public enum ClassificationOutcome
{
    /// <summary>未产生有效裁决（输入不足、规则类实现未命中、或实现无法判定）；调用方按降级契约处理。</summary>
    Unknown = 0,

    /// <summary>仅放行本次调用；不沉淀任何长期规则。</summary>
    AllowOnce = 1,

    /// <summary>放行本次调用，且该裁决可由规则策展器沉淀为长期 allow 规则。</summary>
    AllowPermanent = 2,

    /// <summary>仅拒绝本次调用；不沉淀任何长期规则。</summary>
    DenyOnce = 3,

    /// <summary>拒绝本次调用，且该裁决可由规则策展器沉淀为长期 deny 规则。</summary>
    DenyPermanent = 4,
}

/// <summary>
/// 安全分类器对一次工具调用裁决的完整结论（含溯源信息）。
/// <para>
/// 全部字段只读：分类器产出后即定稿，调用方与规则策展器只消费、不修改。
/// 降级 / 失败场景（分类器不可用、超时、解析失败）也必须产出本记录——
/// 通过 <see cref="ReasonCode"/> 携带稳定原因码，禁止向调用方冒泡异常。
/// </para>
/// </summary>
public sealed record ClassificationVerdict
{
    /// <summary>裁决结论（四选一；降级场景为 <see cref="ClassificationOutcome.Unknown"/>）。</summary>
    public required ClassificationOutcome Outcome { get; init; }

    /// <summary>人类可读的裁决理由；降级 / 失败场景也必须写明原因，禁止留空。</summary>
    public required string Reason { get; init; }

    /// <summary>
    /// 逐分类可信度（0..1）。<b>采用字典而非四个具名字段的理由</b>：
    /// 分类集合未来可能扩展（抽象层同时服务于非准入类决策），且与分类器 wire 载荷
    /// （逐分类校准概率映射）天然对齐，新增分类无需改变本契约形状。
    /// 规范键：<c>allow_once</c> / <c>allow_permanent</c> / <c>deny_once</c> / <c>deny_permanent</c>。
    /// </summary>
    public IReadOnlyDictionary<string, double>? PerOutcomeConfidence { get; init; }

    /// <summary>产出本裁决的分类器稳定标识（对齐 <see cref="IToolCallClassifier.ClassifierId"/>）。</summary>
    public required string ClassifierId { get; init; }

    /// <summary>分类器使用的模型标识；无模型的实现（纯规则类）为 null。</summary>
    public string? ClassifierModel { get; init; }

    /// <summary>本次裁决耗时（毫秒）；实现未计时时为 null。</summary>
    public double? LatencyMs { get; init; }

    /// <summary>稳定协议原因码（如分类器不可用、超时、解析失败）；成功裁决可为 null。</summary>
    public string? ReasonCode { get; init; }

    /// <summary>本裁决所依据（或所更新）的既有规则 id；未命中任何规则时为 null。</summary>
    public string? AppliedRuleId { get; init; }
}

/// <summary>
/// 一次工具调用裁决的完整输入上下文。
/// <para>
/// 仅承载纯数据：对参数 / 轨迹的<b>有界截断由调用方负责</b>——契约层不裁剪、不校验长度，
/// 保证实现侧拿到的是调用方已约束过大小的载荷。
/// </para>
/// </summary>
public sealed record ToolCallClassificationContext
{
    /// <summary>被调用的工具 id。</summary>
    public required string ToolId { get; init; }

    /// <summary>命令名；命令壳类工具为实际命令文本，其余工具可为 null。</summary>
    public string? CommandName { get; init; }

    /// <summary>调用参数（JSON 字符串，原样承载；可为 null）。</summary>
    public string? ArgumentsJson { get; init; }

    /// <summary>本次执行的工作目录（委派 worktree / 执行根）。</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>目标 shell（如 pwsh / cmd / wsl）；非 shell 类工具为 null。</summary>
    public string? Shell { get; init; }

    /// <summary>操作背景说明（审批出题单中的 operation_context）。</summary>
    public string OperationContext { get; init; } = "";

    /// <summary>该操作的目的（为什么要做）。</summary>
    public string Purpose { get; init; } = "";

    /// <summary>必要性说明（为什么必须现在做 / 为什么必须用这个工具）。</summary>
    public string Necessity { get; init; } = "";

    /// <summary>事实依据列表（审批出题单中的 fact_basis）。</summary>
    public IReadOnlyList<string> FactBasis { get; init; } = [];

    /// <summary>目标资源列表（将读取 / 写入 / 影响的对象）。</summary>
    public IReadOnlyList<string> TargetResources { get; init; } = [];

    /// <summary>是否不可逆操作（无法回滚）。</summary>
    public bool IsIrreversibleOperation { get; init; }

    /// <summary>是否可能损坏或删除数据。</summary>
    public bool MayDamageOrDeleteData { get; init; }

    // —— 身份四元组（判定作用域与审计溯源的边界）——

    /// <summary>工作区 id（裁决不跨工作区）。</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>会话 id。</summary>
    public required string SessionId { get; init; }

    /// <summary>Agent 实例 id。</summary>
    public required string AgentInstanceId { get; init; }

    /// <summary>用户 id。</summary>
    public required string UserId { get; init; }

    /// <summary>
    /// Agent 最近轨迹 + 思考摘录（<b>有界字符串</b>）。调用方负责限长与脱敏；
    /// 契约层原样承载，不做二次截断。
    /// </summary>
    public string? RecentTrajectory { get; init; }

    /// <summary>已命中的既有规则摘要（供分类器参考既有确定性结论）；未命中任何规则为空。</summary>
    public IReadOnlyList<string> MatchedRuleSummaries { get; init; } = [];
}

/// <summary>
/// 安全分类器抽象（跨层契约）：系统规则 / 模型 / 未来其它厂商等全部分类器实现的统一裁决端口。
/// <para>
/// 系统只依赖本抽象，不依赖任何具体实现；实现必须 fail-safe——
/// 禁止向调用方冒泡异常，不可用时按降级契约返回确定结论（见
/// <c>Docs/Features/安全分类器与工具调用准入方案-v2.md</c> §4 / §14.7）。
/// </para>
/// </summary>
public interface IToolCallClassifier
{
    /// <summary>分类器稳定标识（用于审计溯源与健康面聚合）。</summary>
    string ClassifierId { get; }

    /// <summary>对一次工具调用裁决，返回完整结论。</summary>
    /// <param name="context">裁决输入上下文。</param>
    /// <param name="ct">取消令牌；实现应在取消时按降级契约返回结论，不得冒泡异常。</param>
    Task<ClassificationVerdict> ClassifyAsync(ToolCallClassificationContext context, CancellationToken ct = default);
}

/// <summary>分类器健康状态（健康面对外只读展示）。</summary>
public enum ClassifierHealth
{
    /// <summary>尚未探测或无法确定。</summary>
    Unknown = 0,

    /// <summary>健康：最近一次裁决成功。</summary>
    Healthy = 1,

    /// <summary>降级：可用但延迟 / 失败率异常。</summary>
    Degraded = 2,

    /// <summary>不可用：连续失败或未配置。</summary>
    Unavailable = 3,
}

/// <summary>单个分类器的健康快照。</summary>
public sealed record ClassifierStatus
{
    /// <summary>分类器稳定标识。</summary>
    public required string ClassifierId { get; init; }

    /// <summary>健康状态。</summary>
    public required ClassifierHealth Health { get; init; }

    /// <summary>人类可读的补充说明（最近一次失败原因 / 降级原因等）。</summary>
    public string? Detail { get; init; }

    /// <summary>连续失败次数（成功一次即清零）。</summary>
    public int ConsecutiveFailures { get; init; }

    /// <summary>最近一次探测 / 裁决时间（UTC）；从未探测为 null。</summary>
    public DateTimeOffset? LastCheckedAtUtc { get; init; }

    /// <summary>最近一次探测 / 裁决的耗时（毫秒）；未计时时为 null。</summary>
    public double? LastLatencyMs { get; init; }
}

/// <summary>
/// 分类器健康面（只读端口）。运行时聚合各分类器实现的状态快照，供健康 API 与前端提示展示；
/// 聚合数据来自各实现的运行期记录，本接口自身不发起网络调用。
/// </summary>
public interface IClassifierHealthReporter
{
    /// <summary>返回当前全部分类器的健康快照（顺序不保证稳定）。</summary>
    IReadOnlyList<ClassifierStatus> Snapshot();
}

/// <summary>
/// Agent 临时完全访问授予记录（<b>仅契约，本切片不实现</b>）。
/// <para>
/// 授予 = 对 agent_instance_id 生效的「放宽审批闸门」模式。语义边界（方案 v2 §14.6）：
/// 只放行审批 / 授权闸门，<b>不</b>放宽角色工具白名单、子代理暴露策略、能力策略、沙箱路径边界等。
/// </para>
/// <para>
/// 生命周期（方案 v2 §14.5）：TTL 由服务端计时，到期自动失效，不依赖 Agent 主动撤销；
/// 进程重启即失效（不持久化）。
/// </para>
/// </summary>
public sealed record AgentFullAccessGrant
{
    /// <summary>授予记录唯一 id。</summary>
    public required string GrantId { get; init; }

    /// <summary>作用域：工作区 id（授予不跨工作区）。</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>作用域：Agent 实例 id（授予不跨 Agent）。</summary>
    public required string AgentInstanceId { get; init; }

    /// <summary>可选：限定到单个会话；null 表示对该 Agent 全部会话生效。</summary>
    public string? SessionId { get; init; }

    /// <summary>授予时间（UTC）。</summary>
    public required DateTimeOffset GrantedAtUtc { get; init; }

    /// <summary>到期时间（UTC）；到期后由服务端自动视为失效。</summary>
    public required DateTimeOffset ExpiresAtUtc { get; init; }

    /// <summary>做出授予裁决的分类器 id（审计溯源）。</summary>
    public required string GrantedByClassifierId { get; init; }

    /// <summary>授予依据的裁决结论；仅 <see cref="ClassificationOutcome.AllowOnce"/> /
    /// <see cref="ClassificationOutcome.AllowPermanent"/> 为合法授予结果。</summary>
    public required ClassificationOutcome Outcome { get; init; }

    /// <summary>授予理由（人类可读，写入审计）。</summary>
    public required string Reason { get; init; }

    /// <summary>显式撤销时间（UTC）；未被撤销为 null。</summary>
    public DateTimeOffset? RevokedAtUtc { get; init; }
}

/// <summary>
/// 临时完全访问授予服务端口（<b>仅契约，本切片不实现</b>）。
/// <para>
/// 实现要求（方案 v2 §14.5 / §14.7）：授予必须经分类器裁决；分类器不可用时一律拒绝（fail-closed）；
/// TTL 服务端计时；进程重启即失效。
/// </para>
/// </summary>
public interface IAgentFullAccessGrantService
{
    /// <summary>查询指定 Agent 当前有效的授予记录（已到期 / 已撤销视为无）；不存在为 null。</summary>
    /// <param name="workspaceId">工作区 id。</param>
    /// <param name="agentInstanceId">Agent 实例 id。</param>
    /// <param name="ct">取消令牌。</param>
    Task<AgentFullAccessGrant?> GetActiveAsync(string workspaceId, string agentInstanceId, CancellationToken ct = default);

    /// <summary>授予临时完全访问；返回生效的授予记录（实现可回填状态字段）。</summary>
    /// <param name="grant">待授予的完整记录（<see cref="AgentFullAccessGrant.Outcome"/> 必须为放行类结论）。</param>
    /// <param name="ct">取消令牌。</param>
    Task<AgentFullAccessGrant> GrantAsync(AgentFullAccessGrant grant, CancellationToken ct = default);

    /// <summary>显式撤销授予。</summary>
    /// <param name="grantId">授予记录 id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>是否撤销成功（记录不存在或已失效为 false）。</returns>
    Task<bool> RevokeAsync(string grantId, CancellationToken ct = default);
}
