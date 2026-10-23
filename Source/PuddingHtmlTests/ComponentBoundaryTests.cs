using System.Reflection;
using System.Text.RegularExpressions;
using PuddingHtml;

namespace PuddingHtmlTests;

/// <summary>
/// S4 边界断言（机器可验）：组件独立性不能只写在注释里，必须能取红。
/// <para>
/// 为什么这几条特别重要：<c>PuddingHtml</c> 存在的唯一理由是让 <c>PuddingRuntime</c> 与
/// <c>PuddingBrowser.AgentTools</c> 共用抽取逻辑，而这两者互不引用。只要本工程多引一个
/// Pudding* 工程，复用就会重新变成反向依赖——而且是在编译期无声地变成那样。
/// </para>
/// </summary>
[TestClass]
public sealed class ComponentBoundaryTests
{
    [TestMethod]
    public void PuddingHtml_Assembly_References_No_Pudding_Assembly()
    {
        var referenced = typeof(HtmlContentExtractor).Assembly
            .GetReferencedAssemblies()
            .Select(name => name.Name ?? string.Empty)
            .ToArray();

        var offending = referenced
            .Where(name => name.StartsWith("Pudding", StringComparison.Ordinal))
            .ToArray();

        Assert.AreEqual(0, offending.Length, $"PuddingHtml must not reference any Pudding* assembly: {string.Join(", ", offending)}");
    }

    [TestMethod]
    public void PuddingHtml_Csproj_Declares_No_ProjectReference()
    {
        var csproj = Path.Combine(RepoRoot(), "Source", "PuddingHtml", "PuddingHtml.csproj");
        Assert.IsTrue(File.Exists(csproj), $"Expected component project at {csproj}");

        var content = StripXmlComments(File.ReadAllText(csproj));

        Assert.IsFalse(content.Contains("<ProjectReference", StringComparison.Ordinal), "a leaf component must not depend on any project");
    }

    [TestMethod]
    public void PuddingHtml_Csproj_Keeps_Package_Surface_Narrow()
    {
        var csproj = Path.Combine(RepoRoot(), "Source", "PuddingHtml", "PuddingHtml.csproj");
        var content = StripXmlComments(File.ReadAllText(csproj));

        var packages = content
            .Split('\n')
            .Where(line => line.Contains("<PackageReference", StringComparison.Ordinal))
            .Select(line => line.Trim())
            .ToArray();

        // Only the parsing libraries themselves; anything host-shaped (logging, DI, runtime)
        // would drag the host back in through the leaf.
        foreach (var package in packages)
        {
            var allowed = package.Contains("HtmlAgilityPack", StringComparison.Ordinal)
                          || package.Contains("ReverseMarkdown", StringComparison.Ordinal);
            Assert.IsTrue(allowed, $"unexpected package reference in the leaf component: {package}");
        }
    }

    [TestMethod]
    public void PuddingHtmlTests_Csproj_References_Exactly_One_Project()
    {
        var csproj = Path.Combine(RepoRoot(), "Source", "PuddingHtmlTests", "PuddingHtmlTests.csproj");
        var content = File.ReadAllText(csproj);

        var references = content.Split("<ProjectReference", StringSplitOptions.None).Length - 1;

        Assert.AreEqual(1, references, "the component test project must reference the component only");
        StringAssert.Contains(content, "PuddingHtml.csproj");
    }

    [TestMethod]
    public void TestProcess_Does_Not_Load_Host_Or_Browser_Assemblies()
    {
        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetName().Name ?? string.Empty)
            .ToArray();

        var offending = loaded
            .Where(name => name.StartsWith("PuddingRuntime", StringComparison.Ordinal)
                           || name.StartsWith("PuddingHost", StringComparison.Ordinal)
                           || name.StartsWith("PuddingBrowser", StringComparison.Ordinal))
            .ToArray();

        Assert.AreEqual(
            0,
            offending.Length,
            $"the component must be testable without the host: {string.Join(", ", offending)}");
    }

    /// <summary>
    /// Removes XML comments before scanning.
    /// <para>
    /// Without this the check reads the explanatory comment that says "ZERO &lt;ProjectReference&gt;"
    /// and reports its own documentation as a violation.
    /// </para>
    /// </summary>
    private static string StripXmlComments(string content) =>
        Regex.Replace(content, "<!--.*?-->", string.Empty, RegexOptions.Singleline);

    /// <summary>Walks up from the test output directory until the solution file is found.</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PuddingAgentNetwork.slnx")))
                return dir.FullName;

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"could not locate the repository root from {AppContext.BaseDirectory}");
    }
}
