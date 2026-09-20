using System.Text.Json.Nodes;

namespace PuddingCode.Abstractions;

/// <summary>
/// 「Jev 决策模型」调用端口（跨层契约）。
/// <para>
/// 与 <see cref="IEmbeddingService"/> 同模式：接口定义在 PuddingCore，
/// 由 PuddingRuntime 的 <c>JevDecisionService</c> 实现（POST {baseUrl}/api/v1/decide）。
/// 与聊天补全不同，Jev 返回的是<b>结构化决策数据</b>（choice / score / noul 三类答案 + 概率 + 置信度），
/// 不是自由文本 —— 调用方拿到 <see cref="JevDecisionResult"/> 后直接读字段，无需再解析自然语言。
/// </para>
/// <para>
/// 端到端 fail-closed：非 2xx 一律抛 <see cref="JevDecisionException"/>（稳定错误码见
/// <see cref="JevDecisionCodes"/>），绝不伪造 answers、绝不返回部分编造的决策结果。
/// </para>
/// <para>
/// 端点 / 密钥 / 模型的解析不在本端口内硬编码，而是经
/// <see cref="IJevDecisionOptionsProvider"/> 注入（生产从配置 + 环境变量 + KeyVault 解析，
/// 单元测试注入假 options，不依赖真实资源池）。
/// </para>
/// </summary>
public interface IJevDecisionService
{
    /// <summary>提交一次决策请求并返回结构化结果。</summary>
    /// <param name="request">状态 + 提问集合（见 <see cref="JevDecisionRequest"/>）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <exception cref="JevDecisionException">请求非法、未配置、鉴权/计费/上游失败或响应不可解析。</exception>
    Task<JevDecisionResult> DecideAsync(JevDecisionRequest request, CancellationToken ct = default);
}

/// <summary>
/// Jev 端点 / 密钥 / 模型的解析缝合点。
/// <para>
/// 默认实现（<c>PuddingRuntime.Services.JevDecisionOptionsProvider</c>）从 IConfiguration
/// 的 <c>Jev</c> 节 + JEV_* 环境变量 + 可选 KeyVault 解析；
/// 基础设施若要把 Jev 纳入资源池（llm.providers.json），只需注册另一个本接口的实现，
/// 不必改动 <see cref="IJevDecisionService"/> 及其实现。
/// </para>
/// </summary>
public interface IJevDecisionOptionsProvider
{
    /// <summary>解析当前生效的 Jev 连接参数。未配置时必须抛
    /// <see cref="JevDecisionException"/>（<see cref="JevDecisionCodes.NotConfigured"/>），不得返回空端点。</summary>
    Task<JevDecisionOptions> GetOptionsAsync(CancellationToken ct = default);
}

/// <summary>Jev 连接参数（每次调用前解析，支持配置热更新）。</summary>
public sealed record JevDecisionOptions
{
    /// <summary>推荐缺省模型；生产建议在配置中固定版本号（如 <c>jev-1.13.0</c>）。</summary>
    public const string DefaultModelId = "jev-latest";

    /// <summary>服务基地址（如 <c>https://api.jev.example</c>），实现会拼接 <c>/api/v1/decide</c>。</summary>
    public required string BaseUrl { get; init; }

    /// <summary>Bearer 鉴权密钥。仅在内存中使用，绝不写日志 / 落盘。</summary>
    public required string ApiKey { get; init; }

    /// <summary>模型 ID。</summary>
    public string ModelId { get; init; } = DefaultModelId;

    /// <summary>502/503/504 上游错误的退避重试次数（0 = 不重试）。</summary>
    public int MaxRetries { get; init; } = 2;

    /// <summary>退避基数（毫秒）；第 n 次重试等待 <c>RetryDelayMilliseconds * 2^n</c>。</summary>
    public int RetryDelayMilliseconds { get; init; } = 500;
}

/// <summary>Jev 提问类型（wire 值为 choice / score / noul）。</summary>
public enum JevQuestionType
{
    /// <summary>离散选项：criteria 为「选项 → 含义描述」映射（值可为 null），最多 255 项。</summary>
    Choice = 0,

    /// <summary>有序评分：criteria 为 2–10 个层级描述（低 → 高）。</summary>
    Score = 1,

    /// <summary>校准概率（0..1，是/否类判断）：必须提供 instructions，criteria 可选。</summary>
    Noul = 2,
}

/// <summary>noul 类型的可选二值判据（wire 形状 <c>{"true":"…","false":"…"}</c>）。</summary>
public sealed record JevBoolCriteria
{
    /// <summary>判为「真」的判据描述。</summary>
    public string? True { get; init; }

    /// <summary>判为「假」的判据描述。</summary>
    public string? False { get; init; }
}

/// <summary>单个提问。三个类型各自只消费对应字段，其余字段忽略。</summary>
public sealed record JevQuestion
{
    /// <summary>提问名（answers 中的键，请求内唯一）。</summary>
    public required string Name { get; init; }

    /// <summary>提问类型。</summary>
    public required JevQuestionType Type { get; init; }

    /// <summary>自然语言说明（noul 必填）。</summary>
    public string? Instructions { get; init; }

    /// <summary>choice 类型判据：选项 → 含义描述（描述可为 null），最多 255 项。</summary>
    public IReadOnlyDictionary<string, string?>? ChoiceCriteria { get; init; }

    /// <summary>score 类型判据：2–10 个层级描述，按低 → 高排序。</summary>
    public IReadOnlyList<string>? ScoreCriteria { get; init; }

    /// <summary>noul 类型可选二值判据。</summary>
    public JevBoolCriteria? NoulCriteria { get; init; }
}

/// <summary>Jev 决策请求。</summary>
public sealed record JevDecisionRequest
{
    /// <summary>
    /// 决策状态：JSON 字符串 / 对象 / 数组任一形态（例如
    /// <c>JsonValue.Create("当前持仓…")</c> 或 <c>JsonNode.Parse("""{"a":1}""")</c>）。
    /// 必填，空值一律 <see cref="JevDecisionCodes.InvalidRequest"/>（fail-closed，不浪费额度）。
    /// </summary>
    public required JsonNode? State { get; init; }

    /// <summary>提问集合，至少一个。</summary>
    public required IReadOnlyList<JevQuestion> Questions { get; init; }

    /// <summary>可选：覆盖 <see cref="JevDecisionOptions.ModelId"/>（例如固定 <c>jev-1.13.0</c> 做可复现实验）。</summary>
    public string? ModelId { get; init; }
}

/// <summary>单个提问的结构化答案。</summary>
public sealed record JevAnswer
{
    /// <summary>提问名（与请求同名键）。</summary>
    public required string Name { get; init; }

    /// <summary>服务端回填的类型字符串（choice / score / noul）。</summary>
    public required string Type { get; init; }

    /// <summary>choice 类型的选中项。</summary>
    public string? Choice { get; init; }

    /// <summary>score 类型的分数（可为小数）。</summary>
    public double? Score { get; init; }

    /// <summary>noul 类型的校准概率（0..1）。</summary>
    public double? Noul { get; init; }

    /// <summary>置信度（0..1）。</summary>
    public double? Confidence { get; init; }

    /// <summary>choice 类型的概率分布（选项 → 概率）。</summary>
    public IReadOnlyDictionary<string, double>? Probabilities { get; init; }

    /// <summary>score 类型的概率分布（按层级有序）。</summary>
    public IReadOnlyList<double>? ScoreProbabilities { get; init; }

    /// <summary>score 类型的分层标签。</summary>
    public IReadOnlyList<string>? Legend { get; init; }
}

/// <summary>用量与余额（服务端返回什么就带什么，缺字段为 null）。</summary>
public sealed record JevUsage
{
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
    public decimal? CostUsd { get; init; }
    public decimal? CreditsRemainingUsd { get; init; }
}

/// <summary>Jev 决策结果（结构化，非文本）。</summary>
public sealed record JevDecisionResult
{
    /// <summary>服务端回填的模型标识（如 <c>jev-1.13.0</c>）。</summary>
    public required string Model { get; init; }

    /// <summary>按提问名索引的答案。服务端未返回的提问不会在此伪造。</summary>
    public required IReadOnlyDictionary<string, JevAnswer> Answers { get; init; }

    /// <summary>用量（服务端未返回时为 null）。</summary>
    public JevUsage? Usage { get; init; }

    /// <summary>取答案；缺失返回 null（不构造占位答案）。</summary>
    public JevAnswer? GetAnswer(string name)
        => Answers.TryGetValue(name, out var answer) ? answer : null;
}

/// <summary>
/// Jev 调用失败异常。<see cref="Code"/> 为稳定 wire 码（见 <see cref="JevDecisionCodes"/>），
/// <see cref="ResponseBody"/> 为截断后的响应体（≤4096 字符，便于排障且不拖垮上下文）。
/// </summary>
public sealed class JevDecisionException : Exception
{
    public JevDecisionException(
        string code,
        string message,
        int? httpStatusCode = null,
        string? responseBody = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        HttpStatusCode = httpStatusCode;
        ResponseBody = responseBody;
    }

    /// <summary>稳定错误码（见 <see cref="JevDecisionCodes"/>）。</summary>
    public string Code { get; }

    /// <summary>HTTP 状态码（本地校验失败时为 null）。</summary>
    public int? HttpStatusCode { get; }

    /// <summary>截断后的响应体（≤4096 字符）。</summary>
    public string? ResponseBody { get; }
}

/// <summary>Jev 稳定 wire 错误码（调用方按码分支，不得重命名）。</summary>
public static class JevDecisionCodes
{
    /// <summary>本地：端点或密钥未配置（fail-closed，未发出请求）。</summary>
    public const string NotConfigured = "jev.not_configured";

    /// <summary>本地：请求不满足协议约束（缺 state / 无 questions / criteria 形状非法）。
    /// 服务端 400 也映射为本码。</summary>
    public const string InvalidRequest = "jev.invalid_request";

    /// <summary>HTTP 401：密钥缺失或失效。</summary>
    public const string Unauthorized = "jev.unauthorized";

    /// <summary>HTTP 402：余额不足（服务端 code=insufficient_credits）。</summary>
    public const string InsufficientCredits = "jev.insufficient_credits";

    /// <summary>HTTP 403：账号停用。</summary>
    public const string AccountDisabled = "jev.account_disabled";

    /// <summary>HTTP 502/503/504：上游错误（已按配置退避重试后仍失败）。</summary>
    public const string UpstreamError = "jev.upstream_error";

    /// <summary>其他非 2xx。</summary>
    public const string HttpError = "jev.http_error";

    /// <summary>2xx 但响应体不可解析 / 缺少 answers（fail-closed，绝不伪造答案）。</summary>
    public const string InvalidResponse = "jev.invalid_response";
}
