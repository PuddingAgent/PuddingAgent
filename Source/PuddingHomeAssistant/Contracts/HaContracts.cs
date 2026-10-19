using System.Text.Json;
using System.Text.Json.Serialization;

namespace PuddingHomeAssistant.Contracts;

/// <summary>
/// 已冻结的 Home Assistant 实例描述。由**引用方**（Host/Runtime）解析配置与凭据后构造，
/// 组件本身不读配置、不解析 DataRoot、不接受调用方在单次调用中覆盖这些值。
///
/// <para>
/// 设计要点：工具参数只能传 <see cref="Alias"/>。若允许调用方传 baseUrl/token，
/// 等价于把 Core 变成可访问任意内网地址的代理（SSRF），并使凭据可能进入日志。
/// </para>
/// </summary>
public sealed class HaInstance
{
    /// <summary>实例别名，工具参数使用的唯一寻址键。</summary>
    public required string Alias { get; init; }

    /// <summary>实例根地址（通常 http://host:8123）。调用方必须已校验协议与来源。</summary>
    public required Uri BaseUrl { get; init; }

    /// <summary>
    /// 长期访问令牌。**绝不**写入日志、错误消息或工具回执；<see cref="ToString"/> 已做掩码。
    /// 声明 <see cref="JsonIgnoreAttribute"/> 以防被误序列化到诊断载荷。
    /// </summary>
    [JsonIgnore]
    public string? Token { get; init; }

    /// <summary>
    /// 用户在 Admin 界面显式放开的额外域（默认空集合）。
    /// 只影响 <see cref="Policy.HaDomainPolicy"/> 的 OptIn 集合，不能解除永久黑名单。
    /// </summary>
    public IReadOnlySet<string> OptInDomains { get; init; } =
        new HashSet<string>(StringComparer.Ordinal);

    public override string ToString() =>
        $"HaInstance(alias={Alias}, baseUrl={BaseUrl}, token={(string.IsNullOrEmpty(Token) ? "<none>" : "<redacted>")})";
}

/// <summary>HA 返回的单个实体状态（已解析，未投影）。</summary>
public sealed record HaState(
    string EntityId,
    string State,
    DateTimeOffset? LastChanged,
    DateTimeOffset? LastUpdated,
    IReadOnlyDictionary<string, JsonElement>? Attributes)
{
    /// <summary>从实体 ID 取出 domain（`light.study` → `light`）；无点时返回空串。</summary>
    public string Domain
    {
        get
        {
            var i = EntityId.IndexOf('.');
            return i <= 0 ? string.Empty : EntityId[..i];
        }
    }
}

/// <summary>
/// 投影后的实体视图（只保留调用方要看的字段，避免把 attributes 全量回传）。
/// <para><c>Name</c>（HA 的 <c>friendly_name</c>）**始终**带上：实体 ID 如
/// <c>light.linp_cn_889128040_t2dbw1_s_8_indicator_light</c> 无法让人（或模型）
/// 判断它是“主卧室灯光”还是“阳台灯”，而挑错实体就等同于挑错设备。
/// 它是不含凭据/坐标的短字符串，与“禁止回传 attributes”不冲突。</para>
/// </summary>
public sealed record HaStateView(
    string EntityId,
    string State,
    DateTimeOffset? LastChanged,
    DateTimeOffset? LastUpdated,
    IReadOnlyDictionary<string, JsonElement>? Attributes,
    string? Name = null);

/// <summary>一页状态结果。必须携带 total/returned/truncated，禁止静默截断。</summary>
public sealed record HaStatePage(
    IReadOnlyList<HaStateView> Items,
    int Total,
    int Returned,
    bool Truncated,
    int EstimatedBytes);

/// <summary>一次动作调用请求。domain/service 会被规范化与策略校验。</summary>
public sealed record HaServiceCall(
    string Domain,
    string Service,
    IReadOnlyList<string>? EntityIds,
    IReadOnlyDictionary<string, object?>? Data,
    bool ReturnResponse);

/// <summary>一次动作调用结果。</summary>
public sealed record HaServiceCallResult(
    int ChangedStateCount,
    JsonElement? Response);

/// <summary>实例配置摘要（供 Admin 列表与工具展示；不含凭据）。</summary>
public sealed record HaInstanceDescriptor(
    string Alias,
    string BaseUrl,
    bool HasToken,
    IReadOnlyList<string> OptInDomains);
