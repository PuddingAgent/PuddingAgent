using System.Reflection;
using System.Text.Json;
using PuddingDeepSeekWeb;

namespace PuddingDeepSeekWebTests;

/// <summary>
/// S3/S4 边界断言（组件化交付规程 §3）：DeepSeek Search / Fetch 协议客户端必须保持叶子身份。
/// <para>
/// 组件允许持有**协议知识**（端点 URL 常量、<c>ApiKey</c>/<c>BaseUrl</c> 字段名、<c>Bearer</c> 头格式），
/// 但不允许**直接说 HTTP**（不引用 <c>System.Net.Http</c>、不出现 <c>HttpClient</c>），也不允许依赖
/// 任何 Pudding 上层程序集。每条"什么都没违规"的绿都必须配一条能取红的正向控制，否则探测器坏了也看不出来。
/// </para>
/// </summary>
[TestClass]
public sealed class ComponentBoundaryTests
{
    /// <summary>禁止出现在组件引用表 / 测试进程闭包里的上层与重依赖程序集（精确名或 <c>名字.</c> 前缀）。</summary>
    private static readonly string[] ForbiddenAssemblyNames =
    [
        "PuddingRuntime",
        "PuddingHost",
        "PuddingAgent",
        "PuddingCore",
        "PuddingPlatform",
        "PuddingDesktop",
        "PuddingCodeIntelligence",
        "PuddingCodeIndex",
        "PuddingFullTextIndex",
        "PuddingMemoryEngine",
    ];

    private static readonly string[] ForbiddenAssemblyPrefixes =
    [
        "Microsoft.CodeAnalysis",
        "Microsoft.Build",
        "Microsoft.Data.Sqlite",
        "Lucene.Net",
    ];

    /// <summary>
    /// 组件源码禁止出现的 HTTP 栈 token。注意**不扫描** <c>https://</c>/<c>apiKey</c>/<c>Bearer</c>：
    /// 这些是协议知识（端点常量、字段名、鉴权头格式），把它们也禁掉等于禁掉 DeepSeek API 本身。
    /// </summary>
    private static readonly string[] ForbiddenSourceTokens =
    [
        "HttpClient",
        "HttpMessageHandler",
        "SocketsHttpHandler",
        "System.Net.Http",
    ];

    private const string ComponentAssemblyName = "PuddingDeepSeekWeb";
    private const string TestAssemblyName = "PuddingDeepSeekWebTests";

    /// <summary>探测器自检：禁用名必须恰好被标记，允许名（含本组件与测试框架）不得误报。</summary>
    [TestMethod]
    public void Detector_Flags_Forbidden_Assembly_Names()
    {
        var flagged = FindViolations(
        [
            "PuddingDeepSeekWeb",
            "PuddingDeepSeekWebTests",
            "PuddingDeepSeekWeb.Extra.Contracts", // loose "Pudding" prefix must NOT be forbidden
            "MSTest.TestFramework",
            "Microsoft.VisualStudio.TestPlatform.ObjectModel",
            "System.Runtime",
            "System.Net.Http", // the test host may load it; the component must not reference it
            "PuddingRuntime",
            "PuddingRuntime.Services",
            "PuddingHost",
            "PuddingHost.Tests",
            "PuddingAgent",
            "PuddingCore",
            "PuddingPlatform.Admin",
            "PuddingDesktop",
            "PuddingDesktop.WpfArchive",
            "PuddingCodeIntelligence",
            "PuddingCodeIndex.Services",
            "PuddingFullTextIndex",
            "PuddingMemoryEngine",
            "Microsoft.CodeAnalysis.CSharp",
            "Microsoft.Build.Locator",
            "Microsoft.Data.Sqlite",
            "Lucene.Net",
        ]);

        CollectionAssert.AreEquivalent(
            new[]
            {
                "PuddingRuntime",
                "PuddingRuntime.Services",
                "PuddingHost",
                "PuddingHost.Tests",
                "PuddingAgent",
                "PuddingCore",
                "PuddingPlatform.Admin",
                "PuddingDesktop",
                "PuddingDesktop.WpfArchive",
                "PuddingCodeIntelligence",
                "PuddingCodeIndex.Services",
                "PuddingFullTextIndex",
                "PuddingMemoryEngine",
                "Microsoft.CodeAnalysis.CSharp",
                "Microsoft.Build.Locator",
                "Microsoft.Data.Sqlite",
                "Lucene.Net",
            },
            flagged.ToArray(),
            "the boundary detector must flag exactly the forbidden assembly names, and nothing else");

        Assert.IsEmpty(
            FindViolations(["PuddingDeepSeekWeb", TestAssemblyName, "MSTest.TestFramework", "System.Net.Http"]),
            "allowed assemblies must not be flagged");
    }

    /// <summary>源码 token 探测器自检：HTTP 栈 token 必须取红，URL / 凭据字段名 / Bearer 不得误伤。</summary>
    [TestMethod]
    public void Source_Token_Detector_Flags_Http_Client_But_Not_Protocol_Knowledge()
    {
        const string offending = """
            using System.Net.Http;
            var client = new HttpClient();
            var handler = new HttpMessageHandler();
            var sink = new SocketsHttpHandler();
            """;

        var flagged = FindForbiddenSourceTokens(offending);
        CollectionAssert.AreEquivalent(
            new[] { "HttpClient", "HttpMessageHandler", "SocketsHttpHandler", "System.Net.Http" },
            flagged.ToArray(),
            "every HTTP-stack token must be detected");

        // Positive control for the other direction: protocol knowledge is legitimate in this leaf.
        const string protocolKnowledge = """
            using System.Net;
            public const string DefaultBaseUrl = "https://api.deepseek.com";
            public required string ApiKey { get; init; }
            var authorization = "Bearer " + connection.ApiKey;
            var ok = IPAddress.TryParse(localization, out _);
            """;

        Assert.IsEmpty(
            FindForbiddenSourceTokens(protocolKnowledge),
            "endpoint URLs, ApiKey/BaseUrl field names and the Bearer header format are protocol knowledge, not HTTP-stack usage");
    }

    /// <summary>测试进程（及其它声明闭包）不得加载任何 Pudding 上层程序集或重依赖。</summary>
    [TestMethod]
    public void Test_Process_Must_Not_Load_Forbidden_Assemblies()
    {
        var testAssembly = typeof(ComponentBoundaryTests).Assembly;
        Assert.AreEqual(
            TestAssemblyName,
            testAssembly.GetName().Name,
            "boundary assertions must run inside the component test assembly");

        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetName().Name)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .ToArray();

        var referenced = testAssembly.GetReferencedAssemblies()
            .Select(assembly => assembly.Name)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .ToArray();

        // Force-load the metadata references: the CLR loads lazily, so an AppDomain-only check can stay
        // green while a forbidden reference sits right there in the manifest.
        var forceLoaded = new List<string>();
        foreach (var name in referenced)
        {
            try
            {
                forceLoaded.Add(Assembly.Load(name)!.GetName().Name!);
            }
            catch (Exception)
            {
                // Unresolvable references cannot introduce a forbidden dependency.
            }
        }

        var declared = ReadDependencyClosure();
        var reachable = Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .Where(name => declared.Contains(name, StringComparer.Ordinal))
            .ToArray();

        var observed = loaded
            .Concat(referenced)
            .Concat(forceLoaded)
            .Concat(reachable)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        // Controls: without them a broken probe path would make the assertion vacuous.
        Assert.Contains(
            ComponentAssemblyName,
            observed,
            "control: the DeepSeek web component itself must be in this process' closure");
        Assert.Contains(
            ComponentAssemblyName,
            reachable,
            "control: the DeepSeek web component must be reachable through the probing path");

        var violations = FindViolations(observed);
        Assert.IsEmpty(
            violations,
            "the component test process must not load upper layers; loaded/declared: " + string.Join(", ", violations));
    }

    /// <summary>组件程序集引用表：不得含上层程序集，也不得引用 HTTP 栈。</summary>
    [TestMethod]
    public void Component_Assembly_Must_Not_Reference_Upper_Layers_Or_Http_Stack()
    {
        var componentAssembly = typeof(DeepSeekWebService).Assembly;

        var referenced = componentAssembly
            .GetReferencedAssemblies()
            .Select(assembly => assembly.Name)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .ToArray();

        Assert.Contains(
            "System.Runtime",
            referenced,
            "control: the component assembly must expose its reference table at all");

        var violations = FindViolations(referenced);
        Assert.IsEmpty(
            violations,
            "the component must not reference upper layers or heavy dependencies, found: "
                + string.Join(", ", violations));

        Assert.IsFalse(
            referenced.Contains("System.Net.Http", StringComparer.Ordinal),
            "the component must not reference System.Net.Http; HTTP belongs behind IDeepSeekWebTransport");
    }

    /// <summary>组件源码不得出现任何 HTTP 栈用法（URL 常量与凭据字段名允许）。</summary>
    [TestMethod]
    public void Component_Sources_Must_Not_Use_Http_Client()
    {
        var projectDirectory = FindRepositoryFile(Path.Combine("Source", ComponentAssemblyName));
        Assert.IsTrue(Directory.Exists(projectDirectory), $"expected the component directory at {projectDirectory}");

        var sources = EnumerateComponentSources(projectDirectory);
        Assert.IsNotEmpty(sources, "control: the scan must actually have files to read");
        Assert.IsTrue(
            sources.Any(path => Path.GetFileName(path) == "DeepSeekWebService.cs"),
            "control: the scan must include the service implementation");

        var offenders = new List<string>();
        foreach (var source in sources)
        {
            var flagged = FindForbiddenSourceTokens(File.ReadAllText(source));
            if (flagged.Count > 0)
                offenders.Add($"{Path.GetFileName(source)}: {string.Join(", ", flagged)}");
        }

        Assert.IsEmpty(
            offenders,
            "the component must not speak HTTP directly; offenders: " + string.Join(" | ", offenders));
    }

    /// <summary>组件 csproj 必须是叶工程：零 ProjectReference、零 PackageReference、无 InternalsVisibleTo。</summary>
    [TestMethod]
    public void Component_Csproj_Must_Declare_Zero_Project_And_Package_References()
    {
        var csprojPath = FindRepositoryFile(
            Path.Combine("Source", ComponentAssemblyName, ComponentAssemblyName + ".csproj"));
        Assert.IsTrue(File.Exists(csprojPath), $"expected the component csproj at {csprojPath}");

        var text = File.ReadAllText(csprojPath);

        Assert.IsFalse(
            text.Contains("<ProjectReference", StringComparison.OrdinalIgnoreCase),
            "the leaf component must declare no ProjectReference");
        Assert.IsFalse(
            text.Contains("<PackageReference", StringComparison.OrdinalIgnoreCase),
            "the leaf component must declare no PackageReference");
        Assert.IsFalse(
            text.Contains("InternalsVisibleTo", StringComparison.OrdinalIgnoreCase),
            "the leaf component must not open itself to another assembly; public API only");
        Assert.IsTrue(
            text.Contains("System.Net.Http", StringComparison.OrdinalIgnoreCase),
            "the component csproj must keep removing the System.Net.Http implicit using (leaf guard)");
    }

    /// <summary>测试工程只允许引用组件本人：恰好 1 条 ProjectReference、0 条 PackageReference。</summary>
    [TestMethod]
    public void Test_Csproj_Must_Reference_Only_The_Component()
    {
        var csprojPath = FindRepositoryFile(
            Path.Combine("Source", TestAssemblyName, TestAssemblyName + ".csproj"));
        Assert.IsTrue(File.Exists(csprojPath), $"expected the test csproj at {csprojPath}");

        var text = File.ReadAllText(csprojPath);
        var references = System.Text.RegularExpressions.Regex
            .Matches(text, @"<ProjectReference\s+Include=""([^""]+)""")
            .Select(match => match.Groups[1].Value.Replace('\\', '/'))
            .ToArray();

        CollectionAssert.AreEqual(
            new[] { $"../{ComponentAssemblyName}/{ComponentAssemblyName}.csproj" },
            references,
            "the component test project must reference exactly one project: the component itself");
        Assert.IsFalse(
            text.Contains("<PackageReference", StringComparison.OrdinalIgnoreCase),
            "the test project must not add package references of its own");
    }

    private static bool IsForbidden(string assemblyName) =>
        ForbiddenAssemblyPrefixes.Any(prefix => assemblyName.StartsWith(prefix, StringComparison.Ordinal))
        || ForbiddenAssemblyNames.Any(
            upper => string.Equals(assemblyName, upper, StringComparison.Ordinal)
                     || assemblyName.StartsWith(upper + ".", StringComparison.Ordinal));

    private static List<string> FindViolations(IEnumerable<string> assemblyNames) =>
        assemblyNames
            .Where(IsForbidden)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

    private static List<string> FindForbiddenSourceTokens(string text) =>
        ForbiddenSourceTokens
            .Where(token => text.Contains(token, StringComparison.Ordinal))
            .OrderBy(token => token, StringComparer.Ordinal)
            .ToList();

    /// <summary>只扫描组件自己的源码，排除 <c>bin/</c> 与 <c>obj/</c> 下的生成文件。</summary>
    private static string[] EnumerateComponentSources(string projectDirectory)
    {
        var separators = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
        return Directory
            .EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path =>
                !path.Split(separators).Any(segment => segment is "bin" or "obj"))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Walks up from the test output directory to the repository root, then resolves a relative path.</summary>
    private static string FindRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PuddingAgentNetwork.slnx")))
                return Path.Combine(directory.FullName, relativePath);

            directory = directory.Parent;
        }

        Assert.Fail(
            "could not locate the repository root (PuddingAgentNetwork.slnx) above " + AppContext.BaseDirectory);
        return string.Empty;
    }

    /// <summary>Reads the reached library names out of this test assembly's <c>*.deps.json</c>.</summary>
    private static IReadOnlyList<string> ReadDependencyClosure()
    {
        var assemblyName = typeof(ComponentBoundaryTests).Assembly.GetName().Name!;
        var depsPath = Path.Combine(AppContext.BaseDirectory, assemblyName + ".deps.json");
        Assert.IsTrue(File.Exists(depsPath), $"expected the SDK-generated dependency manifest at {depsPath}");

        using var document = JsonDocument.Parse(File.ReadAllText(depsPath));
        var names = new SortedSet<string>(StringComparer.Ordinal) { assemblyName };

        if (document.RootElement.TryGetProperty("libraries", out var libraries))
        {
            foreach (var library in libraries.EnumerateObject())
                names.Add(StripVersion(library.Name));
        }

        if (document.RootElement.TryGetProperty("targets", out var targets))
        {
            foreach (var target in targets.EnumerateObject())
            {
                foreach (var library in target.Value.EnumerateObject())
                    names.Add(StripVersion(library.Name));
            }
        }

        return names.ToArray();
    }

    private static string StripVersion(string libraryKey)
    {
        var separator = libraryKey.IndexOf('/');
        return separator < 0 ? libraryKey : libraryKey[..separator];
    }
}
