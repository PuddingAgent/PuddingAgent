using System.Text;

namespace PuddingPathFilteringTests;

/// <summary>
/// 冻结 oracle 的读取器。oracle 由真实 <c>git check-ignore -v -z --no-index --stdin</c>
/// （git 2.53.0.windows.2）在 <c>temp/u4-4-oracle/</c> 的语料上一次性生成后冻结；
/// 测试期间<b>不调用 git</b>，因此结果确定、可复现、不依赖宿主机状态。
/// </summary>
internal static class OracleFixture
{
    internal sealed record Row(string Path, bool IsDirectory, bool ExpectedIgnored, string Source, int Line, string Pattern);

    private static string FixturesRoot => Path.Combine(AppContext.BaseDirectory, "Fixtures");

    internal static string ReadPatternSource(string fileName)
        => File.ReadAllText(Path.Combine(FixturesRoot, fileName), Encoding.UTF8);

    internal static IReadOnlyList<Row> ReadRows(string fileName)
    {
        var rows = new List<Row>();
        foreach (var raw in File.ReadAllLines(Path.Combine(FixturesRoot, fileName), Encoding.UTF8))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var f = line.Split('\t');
            rows.Add(new Row(
                f[0],
                string.Equals(f[1], "dir", StringComparison.Ordinal),
                string.Equals(f[2], "ignore", StringComparison.Ordinal),
                f[3],
                int.Parse(f[4]),
                f[5]));
        }

        return rows;
    }

    /// <summary>
    /// 逐条对照：判定（ignore/keep）必须一致，且「定案的规则原文」必须与 git 打印的 pattern 逐字一致。
    /// 返回人读明细表（供报告 §6 引用），并把不一致项通过 <paramref name="failures"/> 回传。
    /// </summary>
    internal static string Compare(string title, IgnoreStack stack, IReadOnlyList<Row> rows, out IReadOnlyList<string> failures)
    {
        var table = new StringBuilder();
        table.AppendLine($"### {title}（{rows.Count} 条）");
        table.AppendLine("path\tisDir\tgit\t我们\t一致\tgit源\ngit行\tgit pattern\t我们定案的规则原文");

        var bad = new List<string>();
        var agree = 0;
        foreach (var row in rows)
        {
            var ignored = stack.TryDecide(row.Path, row.IsDirectory, out var rule, out var culprit);
            var ours = ignored ? "ignore" : "keep";
            var oursPattern = rule is null ? "-" : rule.RawLine;
            var same = ignored == row.ExpectedIgnored && string.Equals(oursPattern, row.Pattern, StringComparison.Ordinal);
            if (same)
                agree++;
            else
                bad.Add($"{row.Path} (dir={row.IsDirectory}) git={row.ExpectedIgnored} git-pattern='{row.Pattern}' ours={ignored} ours-pattern='{oursPattern}' culprit='{culprit}'");

            table.AppendLine(string.Join('\t', row.Path, row.IsDirectory ? "dir" : "file",
                row.ExpectedIgnored ? "ignore" : "keep", ours, same ? "yes" : "NO",
                $"{row.Source}:{row.Line}", row.Pattern, oursPattern));
        }

        table.AppendLine($"一致 {agree}/{rows.Count}（{agree * 100.0 / rows.Count:F2}%）");
        failures = bad;
        return table.ToString();
    }
}
