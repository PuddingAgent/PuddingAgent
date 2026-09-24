using System.Text.RegularExpressions;

namespace PuddingPathFilteringTests;

/// <summary>
/// <b>S4 边界断言</b>（组件化交付规程 §4）：把「叶子」与「判定链零 I/O」变成可失败的机械检查，
/// 而不是注释里的承诺。
/// </summary>
[TestClass]
public sealed class ComponentBoundaryTests
{
    private static readonly string[] PureDecisionSources =
    [
        "GitWildcard.cs",
        "IgnoreRule.cs",
        "IgnoreFileParser.cs",
        "IgnoreStack.cs",
        "PathText.cs",
        "PathNoiseRules.cs",
        "WorkspacePathFilter.cs",
    ];

    /// <summary>叶子项目不得有任何 ProjectReference / PackageReference（D1 / C1）。</summary>
    [TestMethod]
    public void Leaf_project_has_no_project_or_package_references()
    {
        var csproj = Path.Combine(LeafProjectDirectory(), "PuddingPathFiltering.csproj");
        var text = StripComments(File.ReadAllText(csproj));

        Assert.IsFalse(text.Contains("<ProjectReference", StringComparison.Ordinal),
            "PuddingPathFiltering must have ProjectReference = 0");
        Assert.IsFalse(text.Contains("<PackageReference", StringComparison.Ordinal),
            "PuddingPathFiltering must not depend on NuGet packages");
    }

    /// <summary>判定链（除唯一的 I/O 适配器外）不得出现任何文件系统/环境访问。</summary>
    [TestMethod]
    public void Decision_chain_does_not_touch_the_file_system()
    {
        string[] forbidden =
        [
            "System.IO",
            "File.",
            "Directory.",
            "FileStream",
            "DirectoryInfo",
            "FileInfo",
            "StreamReader",
            "StreamWriter",
            "Path.Combine",
            "Path.GetFullPath",
            "Environment.",
        ];

        var directory = LeafProjectDirectory();
        var offenders = new List<string>();

        foreach (var name in PureDecisionSources)
        {
            var path = Path.Combine(directory, name);
            Assert.IsTrue(File.Exists(path), $"expected leaf source '{name}'");

            var text = StripComments(File.ReadAllText(path));
            foreach (var token in forbidden)
            {
                if (text.Contains(token, StringComparison.Ordinal))
                    offenders.Add($"{name}: '{token}'");
            }
        }

        Assert.IsEmpty(offenders, "decision chain must stay I/O free: " + string.Join(", ", offenders));
    }

    /// <summary>反向依赖守卫：叶子不得引用任何 Pudding* 命名空间。</summary>
    [TestMethod]
    public void Leaf_does_not_reference_any_other_pudding_component()
    {
        var offenders = Directory
            .EnumerateFiles(LeafProjectDirectory(), "*.cs")
            .SelectMany(file => Regex.Matches(File.ReadAllText(file), @"using\s+(Pudding[A-Za-z.]*);")
                .Select(m => $"{Path.GetFileName(file)}: using {m.Groups[1].Value};"))
            .ToArray();

        Assert.IsEmpty(offenders, "leaf must not reference other components: " + string.Join(", ", offenders));
    }

    /// <summary>测试工程也必须只引用叶子（否则 S2/S3 的「无宿主」性质失效）。</summary>
    [TestMethod]
    public void Test_project_references_only_the_leaf()
    {
        var csproj = Path.Combine(
            Directory.GetParent(LeafProjectDirectory())!.FullName,
            "PuddingPathFilteringTests",
            "PuddingPathFilteringTests.csproj");
        var text = File.ReadAllText(csproj);

        var references = Regex.Matches(text, @"<ProjectReference\s+Include=""([^""]+)""")
            .Select(m => m.Groups[1].Value)
            .ToArray();

        Assert.HasCount(1, references);
        StringAssert.Contains(references[0], "PuddingPathFiltering.csproj");
    }

    /// <summary>注释不构成依赖，也不构成 I/O；扫描前先剥掉 XML 块注释与 C# 行注释。</summary>
    private static string StripComments(string source)
    {
        var withoutBlockComments = Regex.Replace(source, @"<!--[\s\S]*?-->", " ");
        return Regex.Replace(withoutBlockComments, @"//[^\n]*", " ");
    }

    private static string LeafProjectDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && directory is not null; i++, directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PuddingAgentNetwork.slnx")))
                return Path.Combine(directory.FullName, "Source", "PuddingPathFiltering");
        }

        Assert.Fail("cannot locate the repository root above " + AppContext.BaseDirectory);
        return string.Empty;
    }
}
