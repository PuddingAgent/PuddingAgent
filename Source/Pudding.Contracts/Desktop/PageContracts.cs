using System.Globalization;

namespace Pudding.Contracts.Desktop;

/// <summary>
/// WebView 调用的显式页面目标：ContextId（工作台上下文）+ PageId（页面/Tab）。
///
/// 必须显式指定，禁止通过「当前激活 Tab」隐式定位：后台 Tab 与用户切换会让隐式目标漂移，
/// 这是跨进程调用的确定性要求（计划 §4）。
/// </summary>
public sealed record DesktopPageTarget
{
    public const int MaxLength = 128;

    public DesktopPageTarget(string contextId, string pageId)
    {
        ContextId = ContractText.RequireIdentifier(contextId, MaxLength, nameof(contextId));
        PageId = ContractText.RequireIdentifier(pageId, MaxLength, nameof(pageId));
    }

    public string ContextId { get; }

    public string PageId { get; }

    /// <summary>稳定字符串键：用于按目标串行化、审计与日志关联（不含页面内容）。</summary>
    public string Key => string.Concat(ContextId, "/", PageId);

    public override string ToString() => Key;
}

/// <summary>
/// 页面版本（等效于现有 Snapshot 的 <c>PageVersion</c>，见 AGENTS.md 浏览器工具约束）。
/// <see cref="Unknown"/>(0) 表示「不校验/未知」；交互提交后旧版本必须作废，不得重查旧 Locator。
/// </summary>
public readonly record struct DesktopPageVersion(long Value)
{
    public static readonly DesktopPageVersion Unknown = new(0);

    public bool IsKnown => Value > 0;

    public static DesktopPageVersion Require(long value) => value > 0
        ? new DesktopPageVersion(value)
        : throw new ArgumentOutOfRangeException(nameof(value), value, "Page version must be positive; use Unknown for 'not tracked'.");

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>页面就绪度。导航调用返回「已接受」不代表 DOM 已就绪，就绪必须通过状态/事件表达。</summary>
public enum DesktopPageReadiness
{
    Unknown,
    Loading,
    Interactive,
    Complete,
    Failed,
}

/// <summary>页面状态快照。只读能力，用于确认导航结果与 PageVersion，不返回页面内容。</summary>
public sealed record DesktopPageState(
    DesktopPageTarget Target,
    Uri? Url,
    DesktopPageVersion Version,
    DesktopPageReadiness Readiness);
