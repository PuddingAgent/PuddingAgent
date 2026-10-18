using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace PuddingConversationReadModelTests;

/// <summary>
/// <b>S4 边界断言</b>（组件化交付规程 S2/S4；Plan §3.1 / §10.2-9）：
/// 把「叶子组件、零宿主依赖、测试进程不加载宿主」变成可失败的机械检查，而不是注释里的承诺。
/// </summary>
[TestClass]
public sealed class ComponentBoundaryTests
{
    private const string ComponentProjectName = "PuddingConversationReadModel";
    private const string TestProjectName = "PuddingConversationReadModelTests";
    private const string ComponentNamespace = "PuddingConversationReadModel";

    private static readonly string[] ForbiddenSourceTokens =
    [
        // I/O / 网络 / 数据访问
        "System.IO",
        "System.Net",
        "System.Data",
        "System.Diagnostics",
        "System.Security",
        "File.",
        "Directory.",
        "FileStream",
        "StreamReader",
        "StreamWriter",
        "HttpClient",
        "Socket",
        "DbContext",
        "Newtonsoft",
        // ASP.NET / DI / 宿主基础设施
        "Microsoft.AspNetCore",
        "Microsoft.Extensions",
        "Microsoft.EntityFrameworkCore",
        // 时间 / 环境 / 随机性：组件必须是纯函数（时间由调用方以参数传入）
        "DateTime.Now",
        "DateTime.UtcNow",
        "DateTimeOffset.Now",
        "DateTimeOffset.UtcNow",
        "Stopwatch",
        "Environment.",
        "Guid.NewGuid",
        "new Random",
        "Task.Delay",
        "Task.Run",
        "Thread.",
        "AppContext.",
        "Assembly.",
        "Process.Start",
        "Console.",
    ];

    private static readonly string[] ForbiddenLoadedAssemblies =
    [
        "PuddingHost",
        "PuddingRuntime",
        "PuddingPlatform",
        "PuddingAgent",
        "PuddingDesktop",
        "PuddingCodeIndex",
        "PuddingCodeIntelligence",
        "PuddingPathFiltering",
        "PuddingMemoryEngine",
        "PuddingFullTextIndex",
    ];

    // ============================================================ 项目文件边界

    /// <summary>叶子项目不得有任何 ProjectReference / PackageReference（S1 / 规程 §3.1）。</summary>
    [TestMethod]
    public void Component_csproj_has_no_project_or_package_references()
    {
        var text = StripXmlComments(File.ReadAllText(Path.Combine(ComponentDirectory(), ComponentProjectName + ".csproj")));

        Assert.IsFalse(text.Contains("<ProjectReference", StringComparison.Ordinal),
            "PuddingConversationReadModel must have ProjectReference = 0");
        Assert.IsFalse(text.Contains("<PackageReference", StringComparison.Ordinal),
            "PuddingConversationReadModel must not depend on NuGet packages");
    }

    /// <summary>测试工程只能引用本组件：恰好 1 条 ProjectReference 且指向本组件（S2）。</summary>
    [TestMethod]
    public void Test_project_references_only_the_component()
    {
        var text = StripXmlComments(File.ReadAllText(Path.Combine(TestProjectDirectory(), TestProjectName + ".csproj")));

        var references = Regex.Matches(text, @"<ProjectReference\s+Include=""([^""]+)""")
            .Select(m => m.Groups[1].Value)
            .ToArray();

        Assert.HasCount(1, references);
        StringAssert.Contains(references[0], ComponentProjectName + ".csproj");
        Assert.IsFalse(text.Contains("<PackageReference", StringComparison.Ordinal),
            "the test csproj itself must not declare package references (framework comes from MSTest.Sdk)");
    }

    // ============================================================ 源码依赖边界

    /// <summary>反向依赖守卫：组件源码不得 using 任何其他 Pudding* 命名空间（只允许自身命名空间）。</summary>
    [TestMethod]
    public void Component_sources_reference_only_their_own_pudding_namespace()
    {
        var offenders = new List<string>();
        foreach (var file in EnumerateComponentSources())
        {
            var text = StripComments(File.ReadAllText(file));
            foreach (Match match in Regex.Matches(text, @"using\s+(Pudding[A-Za-z0-9_.]*)\s*;"))
            {
                var referenced = match.Groups[1].Value;
                if (!referenced.Equals(ComponentNamespace, StringComparison.Ordinal)
                    && !referenced.StartsWith(ComponentNamespace + ".", StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetFileName(file)}: using {referenced};");
                }
            }
        }

        Assert.IsEmpty(offenders, "component must not reference other components/host: " + string.Join(", ", offenders));
    }

    /// <summary>组件是纯规则：不得出现 I/O、网络、时间、环境、DI 或数据访问 token。</summary>
    [TestMethod]
    public void Component_sources_are_pure_and_io_free()
    {
        var offenders = new List<string>();
        foreach (var file in EnumerateComponentSources())
        {
            var text = StripComments(File.ReadAllText(file));
            foreach (var token in ForbiddenSourceTokens)
            {
                if (text.Contains(token, StringComparison.Ordinal))
                    offenders.Add($"{Path.GetFileName(file)}: '{token}'");
            }
        }

        Assert.IsEmpty(offenders, "component must stay pure/IO-free: " + string.Join(", ", offenders));
    }

    /// <summary>组件编译产物只允许引用 BCL（不得引用 Pudding* / Microsoft.* / 第三方程序集）。</summary>
    [TestMethod]
    public void Component_assembly_references_are_bcl_only()
    {
        var assembly = typeof(ConversationReadModelReducer).Assembly;

        var offenders = assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => !name.StartsWith("System", StringComparison.Ordinal)
                           && !name.Equals("netstandard", StringComparison.Ordinal)
                           && !name.Equals("mscorlib", StringComparison.Ordinal))
            .ToArray();

        Assert.IsEmpty(offenders, "component assembly must only reference BCL: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// InternalsVisibleTo 只能开放给本组件的测试工程；开放给上层/宿主属于反向依赖（规程 §3.2 反例）。
    /// </summary>
    [TestMethod]
    public void Component_internals_are_visible_only_to_its_test_assembly()
    {
        var assembly = typeof(ConversationReadModelReducer).Assembly;

        var visibleTo = assembly.GetCustomAttributes<InternalsVisibleToAttribute>()
            .Select(attribute => attribute.AssemblyName.Split(',')[0].Trim())
            .ToArray();

        Assert.HasCount(1, visibleTo);
        Assert.AreEqual(TestProjectName, visibleTo[0]);
    }

    /// <summary>组件只导出一个命名空间（冻结合同面），不得把内部实现散成第二个公共命名空间。</summary>
    [TestMethod]
    public void Component_exports_only_its_own_namespace()
    {
        var foreign = typeof(ConversationReadModelReducer).Assembly.GetExportedTypes()
            .Where(type => !string.Equals(type.Namespace, ComponentNamespace, StringComparison.Ordinal))
            .Select(type => type.FullName ?? type.Name)
            .ToArray();

        Assert.IsEmpty(foreign, "component public surface must stay in " + ComponentNamespace + ": " + string.Join(", ", foreign));
    }

    // ============================================================ 测试进程边界

    /// <summary>
    /// S3 的核心断言：测试进程不得加载宿主或被刻意隔离的兄弟组件，
    /// 否则「脱离 Core 独立验证归约/分页规则」不成立。
    /// </summary>
    [TestMethod]
    public void Test_process_does_not_load_host_or_other_pudding_assemblies()
    {
        var loadedPuddingAssemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetName().Name ?? string.Empty)
            .Where(name => name.StartsWith("Pudding", StringComparison.Ordinal))
            .ToArray();

        var unexpected = loadedPuddingAssemblies
            .Where(name => !name.Equals(ComponentProjectName, StringComparison.Ordinal)
                           && !name.Equals(TestProjectName, StringComparison.Ordinal))
            .ToArray();

        Assert.IsEmpty(unexpected, "test process loaded unexpected Pudding assemblies: " + string.Join(", ", unexpected));

        // 反向断言：组件与测试程序集确实已加载（否则上面的空集合没有意义）。
        Assert.IsNotNull(AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetName().Name)
            .FirstOrDefault(name => name == ComponentProjectName), "component assembly must be loaded");
        Assert.IsNotNull(AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetName().Name)
            .FirstOrDefault(name => name == TestProjectName), "test assembly must be loaded");

        var forbiddenLoaded = ForbiddenLoadedAssemblies.Intersect(loadedPuddingAssemblies, StringComparer.Ordinal).ToArray();
        Assert.IsEmpty(forbiddenLoaded, "host components must not be loaded: " + string.Join(", ", forbiddenLoaded));
    }

    // ============================================================ 辅助

    private static IEnumerable<string> EnumerateComponentSources()
        => Directory.EnumerateFiles(ComponentDirectory(), "*.cs", SearchOption.AllDirectories);

    private static string StripXmlComments(string source)
        => Regex.Replace(source, @"<!--[\s\S]*?-->", " ");

    /// <summary>注释不构成依赖，也不构成 I/O；扫描前先剥掉 XML 块注释、块注释与行注释。</summary>
    private static string StripComments(string source)
    {
        var withoutXml = StripXmlComments(source);
        var withoutBlockComments = Regex.Replace(withoutXml, @"/\*[\s\S]*?\*/", " ");
        return Regex.Replace(withoutBlockComments, @"//[^\n]*", " ");
    }

    private static string ComponentDirectory()
        => Path.Combine(RepositoryRoot(), "Source", ComponentProjectName);

    private static string TestProjectDirectory()
        => Path.Combine(RepositoryRoot(), "Source", TestProjectName);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 14 && directory is not null; i++, directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PuddingAgentNetwork.slnx")))
                return directory.FullName;
        }

        Assert.Fail("cannot locate the repository root above " + AppContext.BaseDirectory);
        return string.Empty;
    }
}
