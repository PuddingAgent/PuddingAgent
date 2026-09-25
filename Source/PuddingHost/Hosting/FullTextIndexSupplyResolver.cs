using System.Globalization;

namespace PuddingHost.Hosting;

/// <summary>
/// 单个 scope 探测结果的三种状态。刻意把「不存在」与「存在但不是目录」分开：
/// <c>Directory.Exists</c> 对文件也返回 false，只用它会把两类问题混成一个模糊的「不存在」（R3 要求逐条可区分）。
/// </summary>
public enum FullTextIndexScopePathKind
{
    /// <summary>路径不存在。</summary>
    Missing = 0,

    /// <summary>路径存在，但不是目录（文件 / 其它非目录项）。</summary>
    NotDirectory = 1,

    /// <summary>路径存在且是目录。</summary>
    Directory = 2,
}

/// <summary>
/// 拒绝原因。**枚举占 0 的是 <see cref="Unknown"/>**（未分类），绝不用「0 = 成功」这种易误读的约定。
/// </summary>
public enum FullTextIndexSupplyRejectReason
{
    /// <summary>未分类（不应出现；出现即表示构造侧漏填）。</summary>
    Unknown = 0,

    /// <summary><c>Enabled=true</c> 但 <c>Scopes</c> 为空。</summary>
    ScopesEmpty,

    /// <summary>scope 项为空串 / 空白。</summary>
    ScopeEmpty,

    /// <summary>scope 目录不存在。</summary>
    ScopeNotFound,

    /// <summary>scope 路径存在但不是目录。</summary>
    ScopeNotDirectory,

    /// <summary>scope 与前面的项解析后指向同一目录（忽略大小写）。</summary>
    ScopeDuplicate,

    /// <summary>相对 scope 需要绝对基准，但 <c>WorkspaceRoot</c> 未声明或不是绝对路径。</summary>
    ScopeRelativeBaseUnavailable,

    /// <summary>路径本身非法（非法字符 / 过长等）。</summary>
    ScopeInvalidPath,

    /// <summary><c>MaxIndexBytes</c> &lt;= 0。</summary>
    MaxIndexBytesNotPositive,

    /// <summary><c>MaxIndexBytes</c> 超过硬天花板（<see cref="FullTextIndexSupplyOptions.MaxAllowedIndexBytes"/>）。</summary>
    MaxIndexBytesAboveLimit,

    /// <summary><c>MinRebuildInterval</c> 为负。</summary>
    MinRebuildIntervalNegative,
}

/// <summary>参数级拒绝：**哪个参数、什么值、为什么**（R3 明确禁止只给 <c>false</c>/<c>null</c>）。</summary>
/// <param name="ParameterName">参数名（取自 <c>nameof</c>，不写第二份字符串）。</param>
/// <param name="Value">被拒绝的原始值（InvariantCulture 文本）。</param>
/// <param name="Reason">机器可判定的原因。</param>
/// <param name="Message">人可读原因，沿用仓库式 <c>FullTextIndex:&lt;参数&gt; must …</c> 前缀。</param>
public sealed record FullTextIndexSupplyRejection(
    string ParameterName,
    string Value,
    FullTextIndexSupplyRejectReason Reason,
    string Message);

/// <summary>scope 级拒绝：逐条列出「哪一项、解析成什么、为什么不合格」。</summary>
/// <param name="Scope">配置里的原始写法。</param>
/// <param name="ResolvedPath">解析后的绝对路径；解析失败为 <c>null</c>。</param>
/// <param name="Reason">机器可判定的原因。</param>
/// <param name="Message">人可读原因。</param>
public sealed record FullTextIndexScopeRejection(
    string Scope,
    string? ResolvedPath,
    FullTextIndexSupplyRejectReason Reason,
    string Message);

/// <summary>
/// 供给参数解析结果。刻意做成**结构化**：<see cref="AcceptedScopes"/> 与 <see cref="RejectedScopes"/>
/// 分别列出（R3 要求），参数级问题进 <see cref="Rejections"/>。
/// </summary>
public sealed record FullTextIndexSupplyResolution
{
    /// <summary>默认路径（<c>Enabled=false</c>）：成功，且是空动作。</summary>
    public static FullTextIndexSupplyResolution Disabled { get; } = new()
    {
        Enabled = false,
        Succeeded = true,
    };

    /// <summary>配置里是否显式开启。</summary>
    public bool Enabled { get; init; }

    /// <summary>是否**整体**通过（fail-closed：任何一条拒绝 ⇒ false ⇒ 什么都不做）。</summary>
    public bool Succeeded { get; init; }

    /// <summary>通过校验、已规范化的绝对目录（诊断用；<see cref="Succeeded"/> 为 false 时不得据此建索引）。</summary>
    public IReadOnlyList<string> AcceptedScopes { get; init; } = [];

    /// <summary>被拒绝的 scope，逐条带原因。</summary>
    public IReadOnlyList<FullTextIndexScopeRejection> RejectedScopes { get; init; } = [];

    /// <summary>参数级拒绝（体积上限 / 最小重建间隔 / Scopes 为空）。</summary>
    public IReadOnlyList<FullTextIndexSupplyRejection> Rejections { get; init; } = [];

    /// <summary>空动作：未开启 ⇒ 不建索引、零 I/O。</summary>
    public bool IsNoOp => !Enabled;

    /// <summary>单行摘要（供日志）。关闭 / 通过 / 拒绝三种都有明确文本。</summary>
    public string Describe()
    {
        var section = FullTextIndexSupplyOptions.SectionName;

        if (IsNoOp)
            return $"{section}: disabled (no-op, zero index I/O).";

        if (Succeeded)
        {
            return $"{section}: enabled; {AcceptedScopes.Count} scope(s) accepted: " +
                   $"[{string.Join(", ", AcceptedScopes)}].";
        }

        var reasons = Rejections
            .Select(static r => $"{r.ParameterName}='{r.Value}' ({r.Reason})")
            .Concat(RejectedScopes.Select(static r => $"Scopes['{r.Scope}'] ({r.Reason})"));

        return $"{section}: rejected; nothing will be indexed. Reasons: {string.Join(" | ", reasons)}";
    }
}

/// <summary>
/// U4-7 R3：供给参数的 **fail-closed 纯函数**解析/校验入口。
/// <para>
/// 纯在「只依赖入参」：不碰 DI、不碰网络；唯一的宿主环境探针是 <c>scopeProbe</c> 委托，
/// **测试可整体替换为替身**（于是单测可以在完全不访问文件系统的前提下覆盖全部拒绝路径）。
/// </para>
/// <para>
/// 规则（逐条都能独立取红，见测试 <c>FullTextIndexSupplyResolverTests</c>）：
/// <list type="number">
/// <item><c>Enabled=false</c> ⇒ 永远合法：立即返回空动作，**不做任何 I/O**（连 scope 探针都不调用）。</item>
/// <item><c>Enabled=true</c> 且 <c>Scopes</c> 为空 ⇒ 拒绝（禁止「开了但不工作」静默空转）。</item>
/// <item><c>Scopes</c> 逐条判定：空串 / 不存在 / 非目录 / 重复 / 相对但无绝对基准 ⇒ 逐条带原因拒绝。</item>
/// <item><c>MaxIndexBytes &lt;= 0</c> 或 &gt; 硬天花板 ⇒ 拒绝（带参数名与值）。</item>
/// <item><c>MinRebuildInterval &lt; TimeSpan.Zero</c> ⇒ 拒绝（带参数名与值）。</item>
/// </list>
/// </para>
/// <para>
/// **fail-closed 的口径**：任何一条不合格 ⇒ <see cref="FullTextIndexSupplyResolution.Succeeded"/> = false
/// ⇒ 调用方**什么都不做**（不做「合格项先建着」的部分生效 —— 部分生效会让「配置错了」不可见）。
/// 合格项仍然列在 <see cref="FullTextIndexSupplyResolution.AcceptedScopes"/> 里，仅作诊断。
/// </para>
/// </summary>
public static class FullTextIndexSupplyResolver
{
    /// <summary>生产口径的 scope 探针（仅在 <c>Enabled=true</c> 时才会被调用）。</summary>
    public static FullTextIndexScopePathKind ProbeFileSystem(string path)
        => Directory.Exists(path) ? FullTextIndexScopePathKind.Directory
            : File.Exists(path) ? FullTextIndexScopePathKind.NotDirectory
            : FullTextIndexScopePathKind.Missing;

    /// <summary>
    /// 解析并校验供给参数。
    /// </summary>
    /// <param name="options">配置绑定出来的选项（不提供任何配置即全部默认值）。</param>
    /// <param name="scopeProbe">scope 目录探针；<c>null</c> 用 <see cref="ProbeFileSystem"/>。测试传替身即可零 I/O。</param>
    /// <returns>结构化结果，<b>永不抛异常</b>（坏输入走拒绝路径，不走异常）。</returns>
    public static FullTextIndexSupplyResolution Resolve(
        FullTextIndexSupplyOptions options,
        Func<string, FullTextIndexScopePathKind>? scopeProbe = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        // 规则 1：Enabled=false ⇒ 永远合法。先返回，绝不触发探针（A1 断言探针调用数为 0）。
        if (!options.Enabled)
            return FullTextIndexSupplyResolution.Disabled;

        var rejections = new List<FullTextIndexSupplyRejection>();
        var scopeRejections = new List<FullTextIndexScopeRejection>();
        var accepted = new List<string>();

        // 规则 4：体积上限。用 nameof 取参数名，避免第二份字符串。
        if (options.MaxIndexBytes <= 0)
        {
            rejections.Add(new FullTextIndexSupplyRejection(
                nameof(FullTextIndexSupplyOptions.MaxIndexBytes),
                options.MaxIndexBytes.ToString(CultureInfo.InvariantCulture),
                FullTextIndexSupplyRejectReason.MaxIndexBytesNotPositive,
                $"{FullTextIndexSupplyOptions.SectionName}:{nameof(FullTextIndexSupplyOptions.MaxIndexBytes)} " +
                $"must be a positive byte count; got {options.MaxIndexBytes.ToString(CultureInfo.InvariantCulture)}."));
        }
        else if (options.MaxIndexBytes > FullTextIndexSupplyOptions.MaxAllowedIndexBytes)
        {
            rejections.Add(new FullTextIndexSupplyRejection(
                nameof(FullTextIndexSupplyOptions.MaxIndexBytes),
                options.MaxIndexBytes.ToString(CultureInfo.InvariantCulture),
                FullTextIndexSupplyRejectReason.MaxIndexBytesAboveLimit,
                $"{FullTextIndexSupplyOptions.SectionName}:{nameof(FullTextIndexSupplyOptions.MaxIndexBytes)} " +
                $"must not exceed the hard ceiling " +
                $"{FullTextIndexSupplyOptions.MaxAllowedIndexBytes.ToString(CultureInfo.InvariantCulture)} B " +
                $"(1 TiB = 2^40); got {options.MaxIndexBytes.ToString(CultureInfo.InvariantCulture)}."));
        }

        // 规则 5：最小重建间隔。
        if (options.MinRebuildInterval < TimeSpan.Zero)
        {
            rejections.Add(new FullTextIndexSupplyRejection(
                nameof(FullTextIndexSupplyOptions.MinRebuildInterval),
                options.MinRebuildInterval.ToString("c", CultureInfo.InvariantCulture),
                FullTextIndexSupplyRejectReason.MinRebuildIntervalNegative,
                $"{FullTextIndexSupplyOptions.SectionName}:{nameof(FullTextIndexSupplyOptions.MinRebuildInterval)} " +
                $"must not be negative (TimeSpan.Zero is allowed = rebuild every time); got " +
                $"'{options.MinRebuildInterval.ToString("c", CultureInfo.InvariantCulture)}'."));
        }

        // 规则 2：开启却没有任何目标 ⇒ 拒绝（这正是「开了但不工作」的可见化）。
        var scopes = options.Scopes ?? [];
        if (scopes.Count == 0)
        {
            rejections.Add(new FullTextIndexSupplyRejection(
                nameof(FullTextIndexSupplyOptions.Scopes),
                "[]",
                FullTextIndexSupplyRejectReason.ScopesEmpty,
                $"{FullTextIndexSupplyOptions.SectionName}:{nameof(FullTextIndexSupplyOptions.Scopes)} is empty while " +
                $"{nameof(FullTextIndexSupplyOptions.Enabled)}=true; list at least one target directory " +
                $"(an empty list would silently do nothing)."));
        }

        // 规则 3：逐条 scope。
        var probe = scopeProbe ?? ProbeFileSystem;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var relativeBaseUsable = !string.IsNullOrWhiteSpace(options.WorkspaceRoot)
                                 && Path.IsPathRooted(options.WorkspaceRoot);

        for (var index = 0; index < scopes.Count; index++)
        {
            var raw = scopes[index];

            if (string.IsNullOrWhiteSpace(raw))
            {
                scopeRejections.Add(new FullTextIndexScopeRejection(
                    raw ?? "<null>",
                    null,
                    FullTextIndexSupplyRejectReason.ScopeEmpty,
                    $"entry #{index} is empty/whitespace; give a directory path (an empty entry must not mean " +
                    $"\"index nothing\")."));
                continue;
            }

            string resolved;
            try
            {
                if (Path.IsPathRooted(raw))
                {
                    resolved = Normalize(raw);
                }
                else if (!relativeBaseUsable)
                {
                    scopeRejections.Add(new FullTextIndexScopeRejection(
                        raw,
                        null,
                        FullTextIndexSupplyRejectReason.ScopeRelativeBaseUnavailable,
                        $"entry #{index} is relative but " +
                        $"{FullTextIndexSupplyOptions.SectionName}:{nameof(FullTextIndexSupplyOptions.WorkspaceRoot)} " +
                        $"is '{options.WorkspaceRoot ?? "<null>"}' (must be an absolute directory). " +
                        $"The process working directory is never used as a base."));
                    continue;
                }
                else
                {
                    resolved = Normalize(Path.Combine(options.WorkspaceRoot!, raw));
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                scopeRejections.Add(new FullTextIndexScopeRejection(
                    raw,
                    null,
                    FullTextIndexSupplyRejectReason.ScopeInvalidPath,
                    $"entry #{index} is not a usable path: {ex.Message}"));
                continue;
            }

            if (!seen.Add(resolved))
            {
                scopeRejections.Add(new FullTextIndexScopeRejection(
                    raw,
                    resolved,
                    FullTextIndexSupplyRejectReason.ScopeDuplicate,
                    $"entry #{index} duplicates an earlier entry (resolved to '{resolved}', compared " +
                    $"case-insensitively)."));
                continue;
            }

            switch (probe(resolved))
            {
                case FullTextIndexScopePathKind.Directory:
                    accepted.Add(resolved);
                    break;

                case FullTextIndexScopePathKind.NotDirectory:
                    scopeRejections.Add(new FullTextIndexScopeRejection(
                        raw,
                        resolved,
                        FullTextIndexSupplyRejectReason.ScopeNotDirectory,
                        $"entry #{index} resolved to '{resolved}', which exists but is not a directory."));
                    break;

                default:
                    scopeRejections.Add(new FullTextIndexScopeRejection(
                        raw,
                        resolved,
                        FullTextIndexSupplyRejectReason.ScopeNotFound,
                        $"entry #{index} resolved to '{resolved}', which does not exist."));
                    break;
            }
        }

        return new FullTextIndexSupplyResolution
        {
            Enabled = true,
            Succeeded = rejections.Count == 0 && scopeRejections.Count == 0,
            AcceptedScopes = accepted,
            RejectedScopes = scopeRejections,
            Rejections = rejections,
        };
    }

    /// <summary>规范化：<c>GetFullPath</c> 去 <c>..</c>/相对段 + 去掉尾部分隔符（便于去重比较）。</summary>
    private static string Normalize(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
