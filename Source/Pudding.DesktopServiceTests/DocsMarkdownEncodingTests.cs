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

    private static string Relative(string path) => Path.GetRelativePath(RepoLayout.Root, path);
}
