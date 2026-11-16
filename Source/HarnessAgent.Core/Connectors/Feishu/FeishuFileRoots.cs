namespace HarnessAgent.Core.Connectors.Feishu;

/// <summary>
/// 解析「文件投递允许根」。
///
/// <para>
/// 默认给两个根：当前工作区，以及 <c>{DataRoot}/output</c>。
/// 后者不是一个凑数的目录——交付物（PDF、报表）本来就不在工作区里，
/// 只给工作区会把「发我那份 PDF」变成做不到的事。
/// </para>
///
/// <para>
/// 可以用环境变量 <see cref="OverrideVariable"/> 覆盖（分号分隔的绝对路径）。
/// 覆盖是**替换**而不是追加：需要精确控制可发范围时，不希望默认根悄悄留着。
/// </para>
/// </summary>
public static class FeishuFileRoots
{
    /// <summary>覆盖默认根的变量名（分号分隔）。</summary>
    public const string OverrideVariable = "PUDDING_FEISHU_FILE_ROOTS";

    // 分号在 Windows 是路径分隔符；Linux 上也照这个约定，避免同一份配置跨平台失效。
    private const char Separator = ';';

    /// <param name="dataRoot">数据根，例如 <c>D:\Data</c>。</param>
    /// <param name="workspacesRoot">工作区根，例如 <c>D:\Data\workspaces</c>。</param>
    /// <param name="workspaceId">当前工作区 id。</param>
    /// <param name="overrideValue">覆盖值；为 null 时使用默认根。</param>
    public static IReadOnlyList<string> Resolve(
        string? dataRoot,
        string? workspacesRoot,
        string? workspaceId,
        string? overrideValue)
    {
        var overridden = Split(overrideValue);
        if (overridden.Count > 0)
            return overridden;

        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(workspacesRoot)
            && !string.IsNullOrWhiteSpace(workspaceId))
        {
            roots.Add(Path.Combine(workspacesRoot.Trim(), workspaceId.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(dataRoot))
            roots.Add(Path.Combine(dataRoot.Trim(), "output"));

        return roots;
    }

    private static List<string> Split(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        var roots = new List<string>();
        foreach (var part in value.Split(Separator, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = part.Trim();
            if (trimmed.Length > 0)
                roots.Add(trimmed);
        }

        return roots;
    }
}
