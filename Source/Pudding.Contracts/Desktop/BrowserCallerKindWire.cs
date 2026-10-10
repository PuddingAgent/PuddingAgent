namespace Pudding.Contracts.Desktop;

/// <summary>
/// 调用者类别的**线缆编码**（方案 §3.2）。两边共用一份真源，避免 Core 与 Desktop 各自解析出不同语义。
///
/// 用**字符串**而不是 proto enum：proto3 enum 的默认值会让"没填"与"填了第一个枚举值"无法区分，
/// 而身份字段**必须**能区分（不得用默认值补身份）。未知取值一律解析失败。
/// </summary>
public static class BrowserCallerKindWire
{
    public const string User = "user";

    public const string Agent = "agent";

    public const string SubAgent = "sub_agent";

    public static string NameOf(BrowserCallerKind kind) => kind switch
    {
        BrowserCallerKind.User => User,
        BrowserCallerKind.Agent => Agent,
        BrowserCallerKind.SubAgent => SubAgent,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Caller kind is not registered."),
    };

    /// <summary>解析线缆取值；未知/空的取值返回 <c>false</c>（调用方按协议错误拒绝，不猜默认值）。</summary>
    public static bool TryParse(string? name, out BrowserCallerKind kind)
    {
        switch (name)
        {
            case User:
                kind = BrowserCallerKind.User;
                return true;
            case Agent:
                kind = BrowserCallerKind.Agent;
                return true;
            case SubAgent:
                kind = BrowserCallerKind.SubAgent;
                return true;
            default:
                kind = default;
                return false;
        }
    }
}
