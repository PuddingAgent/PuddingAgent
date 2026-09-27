namespace PuddingDesktop.Foundation;

/// <summary>One grantable option: a runtime tool capability or a skill package from the legacy ledger.</summary>
public sealed record AgentGrantOption(string Id, string Name, string Detail, bool IsAvailable, string Source)
{
    public bool IsCapability => string.Equals(Source, "capability", StringComparison.Ordinal);
    public bool IsSkillPackage => string.Equals(Source, "skillPackage", StringComparison.Ordinal);
}

public sealed record AgentGrantOptions(
    IReadOnlyList<AgentGrantOption> Capabilities, IReadOnlyList<AgentGrantOption> SkillPackages)
{
    public static AgentGrantOptions Empty { get; } = new([], []);

    public AgentGrantOption? Find(string id) =>
        Capabilities.Concat(SkillPackages).FirstOrDefault(option => string.Equals(option.Id, id, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A stored grant set. Empty really means "no grants", which is not the same as "unspecified".</summary>
public sealed record AgentGrantSet(IReadOnlyList<string> CapabilityIds, IReadOnlyList<string> SkillPackageIds)
{
    public static AgentGrantSet Empty { get; } = new([], []);
    public int Total => CapabilityIds.Count + SkillPackageIds.Count;
}

/// <summary>
/// Instance write intent. <see cref="Unspecified"/> sends null so Core keeps the stored value — it does not
/// re-inherit the template, because Core only inherits at creation time.
/// </summary>
public sealed record AgentGrantSelection(bool Unspecified, IReadOnlyList<string> Ids)
{
    public static AgentGrantSelection UnspecifiedSelection { get; } = new(true, []);
    public static AgentGrantSelection None { get; } = new(false, []);
    public static AgentGrantSelection Of(IEnumerable<string> ids) => new(false, AgentGrantText.Normalize(ids));
}

/// <summary>How an instance's grants differ from the template it came from.</summary>
public sealed record AgentGrantComparison(
    IReadOnlyList<string> OnlyInTemplate, IReadOnlyList<string> OnlyInInstance)
{
    public bool IsIdentical => OnlyInTemplate.Count == 0 && OnlyInInstance.Count == 0;
}

public sealed record AgentInstanceGrantState(
    string AgentId,
    string DisplayName,
    string SourceTemplateId,
    AgentGrantSet Grants,
    AgentGrantSet TemplateGrants)
{
    public AgentGrantComparison Comparison => AgentGrantText.Compare(TemplateGrants, Grants);
}

public static class AgentGrantText
{
    /// <summary>
    /// Core inherits from the template only when an instance is created; afterwards the instance snapshot is
    /// independent. The page must say so instead of implying a live inheritance link.
    /// </summary>
    public const string CreationInheritanceNotice =
        "实例只在创建时从模板继承授权，此后是独立快照：“保持实例当前值”不会重新继承模板，只有“采用模板授权”才会写入模板当前值。";

    public const string EmptyIsNotUnspecified =
        "空列表是“明确不授权”，与“未指定（保持当前值）”不是同一件事，界面必须分别表达。";

    /// <summary>Trims, drops blanks, removes case-insensitive duplicates and sorts for a stable display.</summary>
    public static IReadOnlyList<string> Normalize(IEnumerable<string>? ids) =>
        ids is null
            ? []
            : ids.Select(id => id?.Trim() ?? "")
                .Where(id => id.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToArray();

    public static AgentGrantComparison Compare(AgentGrantSet template, AgentGrantSet instance)
    {
        var templateCapabilities = new HashSet<string>(Normalize(template.CapabilityIds), StringComparer.OrdinalIgnoreCase);
        var instanceCapabilities = new HashSet<string>(Normalize(instance.CapabilityIds), StringComparer.OrdinalIgnoreCase);
        var templateSkills = new HashSet<string>(Normalize(template.SkillPackageIds), StringComparer.OrdinalIgnoreCase);
        var instanceSkills = new HashSet<string>(Normalize(instance.SkillPackageIds), StringComparer.OrdinalIgnoreCase);
        return new AgentGrantComparison(
            [.. templateCapabilities.Except(instanceCapabilities, StringComparer.OrdinalIgnoreCase)
                .Concat(templateSkills.Except(instanceSkills, StringComparer.OrdinalIgnoreCase))
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)],
            [.. instanceCapabilities.Except(templateCapabilities, StringComparer.OrdinalIgnoreCase)
                .Concat(instanceSkills.Except(templateSkills, StringComparer.OrdinalIgnoreCase))
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)]);
    }

    public static string DescribeComparison(AgentGrantSet template, AgentGrantSet instance)
    {
        var comparison = Compare(template, instance);
        if (comparison.IsIdentical) return "与模板授权一致";
        var parts = new List<string>();
        if (comparison.OnlyInInstance.Count > 0) parts.Add($"比模板多 {comparison.OnlyInInstance.Count} 项");
        if (comparison.OnlyInTemplate.Count > 0) parts.Add($"比模板少 {comparison.OnlyInTemplate.Count} 项");
        return "已偏离模板：" + string.Join(" · ", parts);
    }

    public static string DescribeSet(AgentGrantSet grants, AgentGrantOptions options)
    {
        if (grants.Total == 0) return "没有授权（明确不授权）";
        var unknown = grants.CapabilityIds.Concat(grants.SkillPackageIds)
            .Where(id => options.Find(id) is null).ToArray();
        return $"能力 {grants.CapabilityIds.Count} 项 · 技能包 {grants.SkillPackageIds.Count} 项" +
               (unknown.Length == 0 ? "" : $" · 其中 {unknown.Length} 项已不在可用目录中：{string.Join("、", unknown)}");
    }

    public static bool Matches(AgentGrantOption option, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        var term = query.Trim();
        return option.Id.Contains(term, StringComparison.OrdinalIgnoreCase)
            || option.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
            || option.Detail.Contains(term, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An unknown id is an error the form must show; Core would accept it silently.</summary>
    public static IReadOnlyList<string> Validate(AgentGrantSet grants, AgentGrantOptions options)
    {
        var errors = new List<string>();
        foreach (var id in grants.CapabilityIds.Concat(grants.SkillPackageIds))
            if (options.Find(id) is null) errors.Add($"授权项 {id} 不在当前可用目录中。");
        return errors;
    }
}
