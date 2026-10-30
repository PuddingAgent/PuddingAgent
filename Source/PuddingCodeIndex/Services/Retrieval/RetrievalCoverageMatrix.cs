namespace PuddingCodeIndex.Services.Retrieval;

/// <summary>
/// 覆盖矩阵的一格：某项目 × 某语言 × 某能力面 的声明结果。
/// <para>
/// <see cref="Supported"/> 与 <see cref="Alternative"/> 的关系由
/// <see cref="PuddingRetrieval.Contracts.LanguageCapability"/> 保证（不支持必有替代），
/// 本类型只把它们投影成"可以打印成表"的形状。
/// </para>
/// </summary>
/// <param name="ProjectId">项目标识。</param>
/// <param name="LanguageId">语言标识（小写）。</param>
/// <param name="Face">能力面。</param>
/// <param name="Level">能力级别。</param>
/// <param name="Supported">是否声明支持。</param>
/// <param name="Alternative">不支持时的替代方案。</param>
public sealed record CoverageCell(
    string ProjectId,
    string LanguageId,
    string Face,
    string Level,
    bool Supported,
    string? Alternative);

/// <summary>一个项目在覆盖矩阵里的全部格子（按语言、面排序）。</summary>
public sealed record ProjectCoverage(string ProjectId, IReadOnlyList<CoverageCell> Cells)
{
    /// <summary>该项目声明支持的面数。</summary>
    public int SupportedCellCount => Cells.Count(cell => cell.Supported);

    /// <summary>该项目下有声明支持的语言（有任一格支持即算）。</summary>
    public IReadOnlyList<string> SupportedLanguages =>
        Cells.Where(cell => cell.Supported)
            .Select(cell => cell.LanguageId)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
}

/// <summary>输入侧：一个项目包含哪些语言。</summary>
public sealed record ProjectLanguages(string ProjectId, IReadOnlyList<string> LanguageIds);

/// <summary>
/// 覆盖矩阵（施工计划 R0-03：「建立项目/语言/provider 覆盖矩阵」）。
/// <para>
/// <b>项目维度在输出层交叉，不扩展端口</b>：<c>ILanguageCapabilityMatrix</c> 只认语言，
/// 那是能力本身的事实；「某个项目里有哪些语言」是索引数据的事实。把两者在输出层相乘，
/// 比对端口加一个 <c>projectId</c> 参数要安全 —— 后者会让每个实现都被迫处理一个
/// 它并不需要的维度。
/// </para>
/// <para>
/// <b>不猜语言</b>：输入没给的语言不会出现在结果里；给了但没有声明的语言，
/// 每一格都是 <c>Unsupported</c> + 统一的「改用 grep」替代方案。
/// </para>
/// </summary>
public static class RetrievalCoverageMatrix
{
    /// <summary>
    /// 构建覆盖矩阵。
    /// </summary>
    /// <param name="projects">项目与其包含的语言。项目为空时不产生任何行。</param>
    /// <param name="matrix">语言能力矩阵。</param>
    /// <exception cref="ArgumentNullException">任一参数为 <c>null</c>。</exception>
    public static IReadOnlyList<ProjectCoverage> Build(
        IEnumerable<ProjectLanguages> projects,
        PuddingRetrieval.Contracts.ILanguageCapabilityMatrix matrix)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(matrix);

        var faces = Enum.GetValues<PuddingRetrieval.Contracts.RetrievalCapabilityFace>();

        var rows = new List<ProjectCoverage>();

        foreach (var project in projects)
        {
            if (project is null || string.IsNullOrWhiteSpace(project.ProjectId))
                continue;

            var languages = (project.LanguageIds ?? [])
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim().ToLowerInvariant())
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();

            var cells = new List<CoverageCell>(languages.Length * faces.Length);

            foreach (var language in languages)
            {
                foreach (var face in faces)
                {
                    var capability = matrix.Resolve(language, face);
                    cells.Add(new CoverageCell(
                        project.ProjectId,
                        language,
                        face.ToString(),
                        capability.Level.ToString(),
                        capability.IsSupported,
                        capability.Alternative));
                }
            }

            rows.Add(new ProjectCoverage(project.ProjectId, cells));
        }

        return rows;
    }

    /// <summary>
    /// 渲染成 Markdown 表格（语言为行、能力面为列），供报告直接落盘。
    /// <para>空格子写 <c>-</c> 而不是留白：留白会被读成「这格没算」，而它其实算过。</para>
    /// </summary>
    public static string ToMarkdown(ProjectCoverage project)
    {
        ArgumentNullException.ThrowIfNull(project);

        var faces = Enum.GetValues<PuddingRetrieval.Contracts.RetrievalCapabilityFace>()
            .Select(face => face.ToString())
            .ToArray();

        var languages = project.Cells
            .Select(cell => cell.LanguageId)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var builder = new System.Text.StringBuilder();
        builder.Append("| 语言 | ").Append(string.Join(" | ", faces)).AppendLine(" |");
        builder.Append("|---|").Append(string.Concat(Enumerable.Repeat("---|", faces.Length))).AppendLine();

        foreach (var language in languages)
        {
            builder.Append("| ").Append(language).Append(" | ");
            foreach (var face in faces)
            {
                var cell = project.Cells.FirstOrDefault(c =>
                    c.LanguageId == language
                    && string.Equals(c.Face, face, StringComparison.Ordinal));

                builder.Append(cell is null ? "-" : cell.Level).Append(" | ");
            }

            builder.AppendLine();
        }

        return builder.ToString();
    }
}
