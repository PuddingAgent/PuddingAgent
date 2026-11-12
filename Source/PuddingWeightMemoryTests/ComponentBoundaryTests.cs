using System.Xml.Linq;
using PuddingWeightMemory;

namespace PuddingWeightMemoryTests;

/// <summary>
/// 依赖方向的运行时复核：叶组件不得引用任何 Pudding 组件，测试项目只引用叶组件。
/// <para>
/// 编译期门禁（csproj 里的 <c>EnforceWeightMemoryLeafBoundary</c>）守"声明"，
/// 这里守"最终产物里到底有没有那条引用"，两者互补：门禁可以被绕过（改 csproj 条件），
/// 但产物里的引用不能。
/// </para>
/// </summary>
[TestClass]
public sealed class ComponentBoundaryTests
{
    private const string ComponentProjectFileName = "PuddingWeightMemory.csproj";
    private const string TestProjectFileName = "PuddingWeightMemoryTests.csproj";

    /// <summary>允许出现的包：Sqlite 驱动 + 它本就会拉取的 native bundle 固定版（NU1903 治理）。</summary>
    private static readonly string[] AllowedPackages =
    [
        "Microsoft.Data.Sqlite",
        "SQLitePCLRaw.bundle_e_sqlite3",
    ];

    /// <summary>任何 Pudding 组件出现在产物引用里都算越界。</summary>
    private static readonly string[] ForbiddenAssemblies =
    [
        "PuddingRuntime",
        "PuddingHost",
        "PuddingCore",
        "PuddingPlatform",
        "PuddingMemoryEngine",
        "Pudding.Contracts",
        "PuddingWeightMemoryTests",
    ];

    [TestMethod]
    public void ComponentProject_DeclaresNoProjectReference()
    {
        var project = Load(ComponentProjectFileName);

        Assert.IsEmpty(project.Descendants("ProjectReference"), "叶组件不得声明任何项目引用。");
    }

    [TestMethod]
    public void ComponentProject_DeclaresOnlySqlitePackages()
    {
        var project = Load(ComponentProjectFileName);
        var packages = project.Descendants("PackageReference")
            .Select(element => (string?)element.Attribute("Include"))
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .ToArray();

        CollectionAssert.AreEquivalent(AllowedPackages, packages, "不得引入别的 NuGet 包（含日志抽象、测试框架）。");
    }

    [TestMethod]
    public void TestProject_ReferencesOnlyTheComponent()
    {
        var project = Load(TestProjectFileName);
        var references = project.Descendants("ProjectReference")
            .Select(element => (string?)element.Attribute("Include"))
            .Where(value => !string.IsNullOrEmpty(value))
            .Select(value => value!)
            .ToArray();

        Assert.HasCount(1, references, "测试工程只允许引用叶组件一个项目。");
        StringAssert.EndsWith(references[0], ComponentProjectFileName);
    }

    [TestMethod]
    public void ComponentAssembly_ReferencesNoPuddingAssembly()
    {
        var referenced = typeof(SqliteWeightMemory).Assembly
            .GetReferencedAssemblies()
            .Select(assembly => assembly.Name ?? string.Empty)
            .ToArray();

        // 阳性对照：Sqlite 驱动必须在引用列表里，否则"没有禁用程序集"可能只是列表取空了。
        Assert.IsTrue(
            referenced.Contains("Microsoft.Data.Sqlite"),
            "阳性对照失败：产物引用列表里没有 Microsoft.Data.Sqlite，本断言不可信。");

        foreach (var forbidden in ForbiddenAssemblies)
        {
            Assert.IsFalse(
                referenced.Contains(forbidden),
                $"越界引用：PuddingWeightMemory 引用了 {forbidden}。");
        }
    }

    private static XDocument Load(string fileName)
    {
        // bin/Debug/net10.0 → 四级上溯即 Source 目录。
        var sourceRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var directory = Path.Combine(sourceRoot, Path.GetFileNameWithoutExtension(fileName));
        var path = Path.Combine(directory, fileName);

        Assert.IsTrue(File.Exists(path), $"找不到项目文件 {path}。");
        return XDocument.Load(path);
    }
}
