namespace PuddingDesktop.Foundation;

public sealed record ToolParameterSummary(string Name, string Type, string Description, bool Required);

/// <summary>
/// One row of the runtime tool registry. Read-only: capabilities are derived from the registry and
/// cannot be created, edited or deleted through the product's admin surface.
/// </summary>
public sealed record ToolCatalogEntry(
    string ToolId,
    string Name,
    string Description,
    string Category,
    string SourceKind,
    string SourceId,
    string RuntimeStatus,
    bool IsEnabledByDefault,
    string PermissionLevel,
    int SortOrder,
    IReadOnlyList<ToolParameterSummary> Parameters)
{
    public bool IsExecutable => ToolPluginText.IsExecutable(RuntimeStatus);
    public bool IsFromPlugin => !string.Equals(SourceKind, "BuiltIn", StringComparison.OrdinalIgnoreCase);
    public string ParameterSummary => Parameters.Count == 0
        ? "无参数"
        : string.Join("、", Parameters.Select(p => p.Required ? p.Name + "*" : p.Name));
}

public sealed record PluginPackageSummary(
    string PluginId, string Name, string Version, string Status, string StatusReason, string ManifestPath, int ToolCount)
{
    public bool IsManifestOnly => string.Equals(Status, "ManifestOnly", StringComparison.OrdinalIgnoreCase);
    public bool IsInvalid => string.Equals(Status, "ManifestInvalid", StringComparison.OrdinalIgnoreCase);
}

public sealed record PluginDiagnosticEntry(
    DateTimeOffset OccurredAtUtc, string EventType, string PluginId, string Status, string Message);

/// <summary>A tool a plugin package declares. Declared is not installed: the runtime status decides.</summary>
public sealed record PluginToolDeclaration(
    string PluginId, string ToolId, string Name, string RuntimeStatus, bool IsEnabledByDefault)
{
    public bool IsExecutable => ToolPluginText.IsExecutable(RuntimeStatus);
}

public sealed record PluginCatalogReport(
    IReadOnlyList<PluginPackageSummary> Packages,
    IReadOnlyList<PluginToolDeclaration> DeclaredTools,
    IReadOnlyList<PluginDiagnosticEntry> RecentDiagnostics)
{
    /// <summary>Manifest-declared tools are catalog entries only; they are never executable.</summary>
    public int ManifestOnlyToolCount => DeclaredTools.Count(tool => !tool.IsExecutable);

    public int InvalidManifestCount => Packages.Count(package => package.IsInvalid);
}

/// <summary>Task-shaped read model for the tool registry and plugin catalogue, implemented in Composition.</summary>
public interface IToolPluginSettings
{
    Task<IReadOnlyList<ToolCatalogEntry>> ListToolsAsync(CancellationToken cancellationToken = default);
    Task<PluginCatalogReport> ReadPluginCatalogAsync(CancellationToken cancellationToken = default);
    /// <summary>Re-reads plugin.json descriptors from the data root. Read-only with respect to packages.</summary>
    Task ReloadPluginsAsync(CancellationToken cancellationToken = default);
}

public static class ToolPluginText
{
    public const string ManifestOnlyStatus = "ManifestOnly";
    public const string AvailableStatus = "Available";
    public const string InvalidStatus = "ManifestInvalid";

    /// <summary>
    /// Only a tool the runtime registry reports as available is executable. A manifest-only declaration
    /// must never be presented as runnable, and unknown states fail closed.
    /// </summary>
    public static bool IsExecutable(string? runtimeStatus) =>
        string.Equals(runtimeStatus, AvailableStatus, StringComparison.OrdinalIgnoreCase);

    public static string DescribeRuntimeStatus(string? runtimeStatus) => runtimeStatus switch
    {
        null or "" => "状态未知（按不可执行处理）",
        var s when string.Equals(s, AvailableStatus, StringComparison.OrdinalIgnoreCase) => "可执行",
        var s when string.Equals(s, ManifestOnlyStatus, StringComparison.OrdinalIgnoreCase) => "仅清单声明（不可执行）",
        var s => $"{s}（不可执行）"
    };

    public static string DescribePluginStatus(string? status) => status switch
    {
        null or "" => "状态未知",
        var s when string.Equals(s, "Discovered", StringComparison.OrdinalIgnoreCase) => "已发现（未安装运行）",
        var s when string.Equals(s, ManifestOnlyStatus, StringComparison.OrdinalIgnoreCase) => "仅清单声明（工具不可执行）",
        var s when string.Equals(s, InvalidStatus, StringComparison.OrdinalIgnoreCase) => "清单无效",
        var s => s
    };

    /// <summary>Case-insensitive search across the fields an administrator would actually search by.</summary>
    public static bool Matches(ToolCatalogEntry tool, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        var term = query.Trim();
        return Contains(tool.ToolId, term) || Contains(tool.Name, term) || Contains(tool.Description, term)
            || Contains(tool.Category, term) || Contains(tool.SourceId, term) || Contains(tool.SourceKind, term);
    }

    private static bool Contains(string? value, string term) =>
        value is not null && value.Contains(term, StringComparison.OrdinalIgnoreCase);
}
