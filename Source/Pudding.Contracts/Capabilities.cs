namespace Pudding.Contracts;

/// <summary>
/// Desktop 原生能力标识（位标志）。这是<b>协商身份</b>，不是权限：
/// 声明「可实现」不等于「已授权」，权限在 Core 准入与 Desktop 侧目标校验中另行判定。
/// </summary>
[Flags]
public enum DesktopCapability
{
    None = 0,

    /// <summary>导航到指定页面目标。</summary>
    WebViewNavigate = 1 << 0,

    /// <summary>在已授权的页面目标中执行脚本。</summary>
    WebViewExecuteJavascript = 1 << 1,

    /// <summary>读取页面状态（URL、就绪度、PageVersion）。</summary>
    WebViewPageState = 1 << 2,

    /// <summary>显示系统通知。</summary>
    ShellNotification = 1 << 3,

    /// <summary>读取 Shell 状态（窗口/托盘/挂起等）。</summary>
    ShellStatus = 1 << 4,

    /// <summary>显示对话框并返回结构化结果（含用户取消）。</summary>
    ShellDialog = 1 << 5,

    /// <summary>显示文件选择器；返回的是授权句柄/用户选择，不代表 Core 自动可读该路径。</summary>
    ShellFilePicker = 1 << 6,

    /// <summary>剪贴板读写（受可信上下文限制）。</summary>
    ShellClipboard = 1 << 7,
}

public enum DesktopCapabilityKind
{
    WebView,
    Shell,
}

/// <summary>能力的执行语义，供调度与准入使用；同名能力在不同版本下语义必须保持兼容。</summary>
[Flags]
public enum DesktopCapabilityTraits
{
    None = 0,

    /// <summary>改变页面/系统状态：同一目标上的调用必须串行化。</summary>
    Mutating = 1 << 0,

    /// <summary>取消或超时后可能已产生副作用：终态必须如实标注，不得声称「未执行」。</summary>
    HasSideEffects = 1 << 1,

    /// <summary>只允许可信 Workbench / 已授权 Agent 目标；普通网页与第三方客户端不得调用。</summary>
    RequiresTrustedContext = 1 << 2,

    /// <summary>需要用户在场（对话框、Picker），单窗口同时最多一个。</summary>
    RequiresUserInteraction = 1 << 3,

    /// <summary>请求必须携带显式页面目标（ContextId/PageId），并据此判定可信级别。</summary>
    RequiresPageTarget = 1 << 4,
}

/// <summary>握手声明：能力 + 线名 + 版本。线名是 wire 真源的一部分，重命名属于破坏性变更。</summary>
public sealed record DesktopCapabilityDeclaration(DesktopCapability Capability, string Name, int Version);

/// <summary>能力目录条目。线名冻结在 <see cref="DesktopCapabilities"/>，并由契约测试快照断言。</summary>
public sealed record DesktopCapabilityDescriptor(
    DesktopCapability Capability,
    string Name,
    int Version,
    DesktopCapabilityKind Kind,
    DesktopCapabilityTraits Traits);

/// <summary>能力目录（唯一真源）。握手、映射、授权与审计都从这里取名与版本。</summary>
public static class DesktopCapabilities
{
    public const int InitialVersion = 1;

    private static readonly DesktopCapabilityDescriptor[] Catalog =
    [
        new(DesktopCapability.WebViewNavigate, "webview.navigate", InitialVersion,
            DesktopCapabilityKind.WebView, DesktopCapabilityTraits.Mutating | DesktopCapabilityTraits.HasSideEffects | DesktopCapabilityTraits.RequiresPageTarget),
        new(DesktopCapability.WebViewExecuteJavascript, "webview.execute_javascript", InitialVersion,
            DesktopCapabilityKind.WebView, DesktopCapabilityTraits.Mutating | DesktopCapabilityTraits.HasSideEffects | DesktopCapabilityTraits.RequiresTrustedContext | DesktopCapabilityTraits.RequiresPageTarget),
        new(DesktopCapability.WebViewPageState, "webview.page_state", InitialVersion,
            DesktopCapabilityKind.WebView, DesktopCapabilityTraits.RequiresPageTarget),
        new(DesktopCapability.ShellNotification, "shell.notification", InitialVersion,
            DesktopCapabilityKind.Shell, DesktopCapabilityTraits.HasSideEffects),
        new(DesktopCapability.ShellStatus, "shell.status", InitialVersion,
            DesktopCapabilityKind.Shell, DesktopCapabilityTraits.None),
        new(DesktopCapability.ShellDialog, "shell.dialog", InitialVersion,
            DesktopCapabilityKind.Shell, DesktopCapabilityTraits.HasSideEffects | DesktopCapabilityTraits.RequiresUserInteraction | DesktopCapabilityTraits.RequiresTrustedContext),
        new(DesktopCapability.ShellFilePicker, "shell.file_picker", InitialVersion,
            DesktopCapabilityKind.Shell, DesktopCapabilityTraits.HasSideEffects | DesktopCapabilityTraits.RequiresUserInteraction | DesktopCapabilityTraits.RequiresTrustedContext),
        new(DesktopCapability.ShellClipboard, "shell.clipboard", InitialVersion,
            DesktopCapabilityKind.Shell, DesktopCapabilityTraits.Mutating | DesktopCapabilityTraits.HasSideEffects | DesktopCapabilityTraits.RequiresTrustedContext),
    ];

    private static readonly Dictionary<DesktopCapability, DesktopCapabilityDescriptor> ByCapability =
        Catalog.ToDictionary(d => d.Capability);

    private static readonly Dictionary<string, DesktopCapabilityDescriptor> ByName =
        Catalog.ToDictionary(d => d.Name, StringComparer.Ordinal);

    /// <summary>目录顺序即握手声明顺序，便于快照断言。</summary>
    public static IReadOnlyList<DesktopCapabilityDescriptor> All => Catalog;

    /// <summary>所有已登记能力的集合。</summary>
    public static DesktopCapability AllCapabilities { get; } = Catalog.Aggregate(
        DesktopCapability.None, (accumulated, descriptor) => accumulated | descriptor.Capability);

    public static bool TryGet(DesktopCapability capability, out DesktopCapabilityDescriptor descriptor) =>
        ByCapability.TryGetValue(capability, out descriptor!);

    public static bool TryGetByName(string? name, out DesktopCapabilityDescriptor descriptor)
    {
        descriptor = null!;
        return !string.IsNullOrEmpty(name) && ByName.TryGetValue(name, out descriptor!);
    }

    /// <summary>取单个能力的线名；组合位或未登记值取红。</summary>
    public static string NameOf(DesktopCapability capability) =>
        TryGet(capability, out var descriptor)
            ? descriptor.Name
            : throw new ArgumentOutOfRangeException(
                nameof(capability), capability, "Capability must be a single registered value.");

    public static int VersionOf(DesktopCapability capability) =>
        TryGet(capability, out var descriptor)
            ? descriptor.Version
            : throw new ArgumentOutOfRangeException(
                nameof(capability), capability, "Capability must be a single registered value.");

    /// <summary>按目录顺序枚举集合中的单个能力位。</summary>
    public static IEnumerable<DesktopCapability> Enumerate(DesktopCapability set)
    {
        foreach (var descriptor in Catalog)
        {
            if (set.HasFlag(descriptor.Capability))
            {
                yield return descriptor.Capability;
            }
        }
    }

    /// <summary>握手声明：集合 → 线名 + 版本列表；未登记位会被忽略（不静默伪造能力）。</summary>
    public static IReadOnlyList<DesktopCapabilityDeclaration> DeclareFor(DesktopCapability set) =>
        Enumerate(set).Select(c => new DesktopCapabilityDeclaration(c, NameOf(c), VersionOf(c))).ToArray();
}

/// <summary>
/// 握手协商。规则：Core 只能授予 Desktop 已声明过的能力，且版本不得高于声明版本；
/// 任何越权授予都是协议错误，必须让握手失败而不是「部分接受」。
/// </summary>
public static class DesktopCapabilityNegotiation
{
    /// <summary>成功返回 <c>null</c>；失败返回明确的领域错误，且不产生任何可用能力。</summary>
    public static DesktopCapabilityError? Validate(
        IReadOnlyList<DesktopCapabilityDeclaration> declared,
        IReadOnlyList<DesktopCapabilityDeclaration>? granted,
        out DesktopCapability negotiated,
        out IReadOnlyList<DesktopCapabilityDeclaration> negotiatedDeclarations)
    {
        ArgumentNullException.ThrowIfNull(declared);
        negotiated = DesktopCapability.None;
        negotiatedDeclarations = [];

        if (declared.Count == 0)
        {
            return DesktopCapabilityError.InvalidRequest("Desktop declared no capabilities; nothing to negotiate.");
        }

        var declaredVersions = new Dictionary<DesktopCapability, int>();
        foreach (var declaration in declared)
        {
            if (!DesktopCapabilities.TryGet(declaration.Capability, out var known))
            {
                return DesktopCapabilityError.InvalidRequest($"Declared capability '{declaration.Name}' is not registered.");
            }

            if (!string.Equals(known.Name, declaration.Name, StringComparison.Ordinal))
            {
                return DesktopCapabilityError.InvalidRequest(
                    $"Declared name '{declaration.Name}' does not match the registered wire name '{known.Name}'.");
            }

            if (!declaredVersions.TryAdd(declaration.Capability, declaration.Version))
            {
                return DesktopCapabilityError.InvalidRequest($"Capability '{declaration.Name}' was declared twice.");
            }
        }

        if (granted is null || granted.Count == 0)
        {
            return null;
        }

        var accepted = new List<DesktopCapabilityDeclaration>(granted.Count);
        var negotiatedSet = DesktopCapability.None;
        foreach (var grant in granted)
        {
            if (!DesktopCapabilities.TryGet(grant.Capability, out _)
                || !declaredVersions.TryGetValue(grant.Capability, out var declaredVersion))
            {
                return DesktopCapabilityError.UnsupportedCapability(grant.Name);
            }

            if (grant.Version > declaredVersion)
            {
                return DesktopCapabilityError.UnsupportedCapability(grant.Name);
            }

            negotiatedSet |= grant.Capability;
            accepted.Add(grant);
        }

        negotiated = negotiatedSet;
        negotiatedDeclarations = accepted;
        return null;
    }
}
