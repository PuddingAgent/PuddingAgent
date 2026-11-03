namespace PuddingComputer.UIAutomation;

/// <summary>
/// 本组件的依赖边界，以**可执行规则**的形式声明。
/// <para>
/// 为什么不只写在 csproj 注释里：注释不会失败。架构边界一旦只在文档中，
/// 下一次"顺手加个引用"就不会有人拦住它 —— 而这类越界通常几个月后才发现，
/// 那时已经很难拆回来了。
/// </para>
/// <para>
/// 规则分两段：**白名单**限定可引用的前缀，**黑名单**再明确禁止几类。
/// 黑名单存在的理由是"命中白名单不等于允许"：比如将来出现
/// <c>Pudding.Contracts.Shared</c> 之类命名，白名单会放行它，但它可能
/// 依赖了别的层。
/// </para>
/// </summary>
public static class BoundaryRules
{
    /// <summary>
    /// 允许引用的程序集名前缀。
    /// <para>
    /// - <c>Pudding.Contracts</c>：共享契约（<c>PhysicalRect</c> 等）<br/>
    /// - <c>FlaUI</c> / <c>Interop</c>：UIA 实现及其 COM 互操作<br/>
    /// - <c>System</c> / <c>mscorlib</c> / <c>netstandard</c> / <c>WindowsBase</c>：BCL
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<string> AllowedPrefixes =
    [
        "Pudding.Contracts",
        "FlaUI",
        "Interop",
        "System",
        "mscorlib",
        "netstandard",
        "WindowsBase",
    ];

    /// <summary>
    /// 明确禁止的程序集名前缀。
    /// <para>
    /// 这些一旦进入本组件，就说明边界被穿透了：本层只做"读无障碍树"，
    /// 不该知道截图、OpenCV、基准任务、编排或宿主。
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<string> ForbiddenPrefixes =
    [
        "PuddingComputer.Windows",     // 截图 / 输入 / OpenCV
        "PuddingComputer.Automation",  // 纯规则层，本层不该反向依赖
        "PuddingComputer.Benchmark",   // 任务与 oracle 定义
        "PuddingRuntime",
        "PuddingCore",
        "PuddingPlatform",
        "PuddingDesktop",
    ];

    /// <summary>
    /// 找出违规引用。
    /// </summary>
    /// <param name="assemblyNames">被检查程序集引用到的程序集名。</param>
    /// <returns>违规项（可读描述）；空表示边界完好。</returns>
    public static IReadOnlyList<string> FindViolations(IEnumerable<string> assemblyNames)
    {
        ArgumentNullException.ThrowIfNull(assemblyNames);

        var violations = new List<string>();

        foreach (var name in assemblyNames)
        {
            if (string.IsNullOrWhiteSpace(name))
                continue;

            // 黑名单优先：命中即违规，不再看白名单。
            if (ForbiddenPrefixes.Any(f => name.StartsWith(f, StringComparison.OrdinalIgnoreCase)))
            {
                violations.Add($"{name}：命中禁止前缀");
                continue;
            }

            if (!AllowedPrefixes.Any(a => name.StartsWith(a, StringComparison.OrdinalIgnoreCase)))
                violations.Add($"{name}：不在白名单内");
        }

        return violations;
    }
}
