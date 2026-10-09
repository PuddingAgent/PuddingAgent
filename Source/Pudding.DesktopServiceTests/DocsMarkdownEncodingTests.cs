namespace DesktopServiceTests;

/// <summary>
/// **Docs 下 markdown 的编码底线**：不得带 UTF-8 BOM，且必须以 Front Matter 开头。
///
/// 为什么需要这条（本会话被咬过三次）：
/// <list type="number">
///   <item>round-83：用 PowerShell 改写 fixture 时写入 BOM，被 fixture 的编码用例抓到；</item>
///   <item>round-84：往 <c>.ps1</c> 里加中文，Windows PowerShell 按 ANSI 读无 BOM 脚本 ⇒ 解析失败；</item>
///   <item>round-85/86：用 <c>Set-Content -Encoding UTF8</c> 改 changelog 的相对链接 ⇒ **写入 BOM**，
///         Front Matter 门禁报"缺少 Front Matter"（BOM 让 YAML 不再从字节 0 开始）。</item>
/// </list>
///
/// 第三条之前只能靠 python 门禁**间接**发现（而且是在下一个任务里才被发现）。
/// 这条用例把"没有 BOM"变成提交前就会红的检查。
/// </summary>
public sealed class DocsMarkdownEncodingTests
{
    [Fact]
    public void DocsMarkdownHasNoBomAndStartsWithFrontMatter()
    {
        var docsRoot = Path.Combine(RepoLayout.Root, "Docs");
        Assert.True(Directory.Exists(docsRoot), $"找不到 Docs 目录：{docsRoot}");

        var files = Directory.EnumerateFiles(docsRoot, "*.md", SearchOption.AllDirectories).ToArray();
        Assert.True(files.Length > 100, $"扫描到的 Docs markdown 太少（{files.Length}），路径可能不对");

        var withBom = new List<string>();
        var withoutFrontMatter = new List<string>();

        foreach (var file in files)
        {
            var bytes = File.ReadAllBytes(file);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                withBom.Add(Relative(file));
            }

            if (!File.ReadAllText(file).StartsWith("---", StringComparison.Ordinal))
            {
                withoutFrontMatter.Add(Relative(file));
            }
        }

        Assert.True(
            withBom.Count == 0,
            $"以下 Docs 文件带 UTF-8 BOM（Front Matter 会因此失效）。修法：用 "
            + "`[System.IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding($false)))` "
            + "重写，或直接用编辑工具（不要用 Set-Content -Encoding UTF8）：\n  - "
            + string.Join("\n  - ", withBom));

        Assert.True(
            withoutFrontMatter.Count == 0,
            "以下 Docs 文件缺少 Front Matter（必须以 --- 开头）：\n  - "
            + string.Join("\n  - ", withoutFrontMatter));
    }

    [Fact]
    public void TheBrowserAutomationDeliverablesHaveNoBrokenRelativeLinks()
    {
        // **教训（2026-10-08）**：我在 round 72 之后写 changelog 时把相对层级写错（`../../12_features/`
        // 而正确是 `../../../`），一路积累了 21 个文件、约 33 条失效链接；而我每轮只 grep"自己刚写的那个文件名"，
        // 于是每轮都得出"零失效目标"的**错误结论**，直到本轮看全量报告才发现（66 条里大半是我的）。
        //
        // 这条检查的范围刻意收窄到**本会话交付物**所在的路径（浏览器自动化相关文档），
        // 不替其它子系统背既有的失效链接（那些不属于本次改动，且仓库纪律要求不代改他方内容）。
        var deliverables = new (string Directory, string FileNamePattern)[]
        {
            // `(?!T\d)` 排除他方团队的 changelog（本目录里同时有 `2026-10-08-T6-*` 的并行 WIP）：
            // 他们的失效链接不属本次改动，也不该让我的套件变红（同一条边界教训，见 changelog）。
            ("Docs/00_changelog/2026Year/10", @"^2026-10-0[89]-(?!T\d).*\.md$"),
            ("Docs/12_features", @"^(浏览器|B6-).*\.md$"),
            ("Docs/13_runbooks", @"^浏览器.*\.md$"),
            ("Docs/14_reports", @"^2026-10-0[89]-浏览器.*\.md$"),
        };

        var offenders = new List<string>();
        var scanned = 0;

        foreach (var (directory, pattern) in deliverables)
        {
            var full = Path.Combine(RepoLayout.Root, directory.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(full))
            {
                continue;
            }

            // 注意：文件系统通配符只支持 `*`/`?`，所以这里先取全部 *.md、再用正则过滤。
            var regex = new System.Text.RegularExpressions.Regex(pattern);
            foreach (var file in Directory.EnumerateFiles(full, "*.md")
                .Where(path => regex.IsMatch(Path.GetFileName(path))))
            {
                scanned++;
                var text = RemoveCodeSpans(File.ReadAllText(file));
                foreach (var target in ExtractRelativeTargets(text))
                {
                    var resolved = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, target));
                    if (!File.Exists(resolved) && !Directory.Exists(resolved))
                    {
                        offenders.Add($"{Path.GetRelativePath(RepoLayout.Root, file)} -> {target}");
                    }
                }
            }
        }

        Assert.True(scanned > 20, $"扫描到的交付文档太少（{scanned}），模式可能写错了");
        Assert.True(
            offenders.Count == 0,
            "本会话交付物里存在失效的相对链接（`Docs/00_changelog/2026Year/10/` 到仓库根是 4 级、"
            + "到 `Docs/…` 是 3 级）：\n  - " + string.Join("\n  - ", offenders));
    }

    /// <summary>去掉围栏代码块与行内代码，避免把示例文本里的 `](src)` 当成真链接。</summary>
    private static string RemoveCodeSpans(string markdown)
    {
        var withoutFences = System.Text.RegularExpressions.Regex.Replace(
            markdown, "(?s)```.*?```", string.Empty);
        return System.Text.RegularExpressions.Regex.Replace(withoutFences, "`[^`]*`", string.Empty);
    }

    private static IEnumerable<string> ExtractRelativeTargets(string markdown)
    {
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
            markdown, @"\]\((?<target>[^)\s]+)\)"))
        {
            var target = match.Groups["target"].Value;
            if (target.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                || target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
                || target.StartsWith('#'))
            {
                continue;
            }

            // 去掉锚点与 `file.cs:123` 形式的行号后缀（与仓库检查脚本同规则）。
            var withoutAnchor = target.Split('#')[0];
            var trimmed = System.Text.RegularExpressions.Regex.Replace(withoutAnchor, @":\d+(-\d+)?$", string.Empty);
            if (!string.IsNullOrWhiteSpace(trimmed))
            {
                yield return trimmed;
            }
        }
    }

    private static string Relative(string path) => Path.GetRelativePath(RepoLayout.Root, path);
}
