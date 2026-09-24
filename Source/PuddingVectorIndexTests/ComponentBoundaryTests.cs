using System.Reflection;
using System.Text.Json;
using PuddingVectorIndex;

namespace PuddingVectorIndexTests;

/// <summary>
/// S3/S4 boundary assertions for the vector-index component (组件化交付规程 §3 S3/S4, task book A1).
/// <para>
/// The promise of <c>PuddingVectorIndex</c> is that it is a <b>leaf</b>: cosine, ranking, batching and
/// dimension checks can be built and tested with no vector service, no Lucene, no database and no
/// upper layer present, because the embeddings arrive through a port. A green claim of "nothing
/// forbidden is loaded" is worthless without a detector that can report a violation, so
/// <c>Detector_Flags_Forbidden_Names</c> is the positive control and the csproj/source scans are the
/// mechanical ones.
/// </para>
/// </summary>
[TestClass]
public sealed class ComponentBoundaryTests
{
    private static readonly string[] ForbiddenAssemblyNames =
    [
        "PuddingCodeIntelligence",
        "PuddingCodeIndex",
        "PuddingIndexChunking",
        "PuddingFullTextIndex",
        "PuddingRetrievalEval",
        "PuddingRuntime",
        "PuddingHost",
        "PuddingAgent",
    ];

    private static readonly string[] ForbiddenAssemblyPrefixes =
    [
        "Microsoft.CodeAnalysis",
        "Microsoft.Build",
        "Microsoft.Data.Sqlite",
        "System.Data.SqlClient",
        "System.Data.SQLite",
        "Lucene.Net",
    ];

    /// <summary>
    /// Tokens that must not appear in the component's sources: they are how "直连 HTTP" and "持
    /// apiKey/baseUrl" would actually look on disk (task book §2.1).
    /// </summary>
    private static readonly string[] ForbiddenSourceTokens =
    [
        "HttpClient",
        "System.Net.Http",
        "http://",
        "https://",
        "apiKey",
        "baseUrl",
        "Bearer",
    ];

    private const string ComponentAssemblyName = "PuddingVectorIndex";

    /// <summary>Self-check of the detector: it must flag exactly the forbidden names, and nothing else.</summary>
    [TestMethod]
    public void Detector_Flags_Forbidden_Names()
    {
        var flagged = FindViolations(
        [
            "PuddingVectorIndex",
            "PuddingVectorIndexTests",
            "MSTest.TestFramework",
            "Microsoft.CodeAnalysis.CSharp",
            "Microsoft.Build.Locator",
            "Lucene.Net",
            "Microsoft.Data.Sqlite",
            // Framework assemblies the test host legitimately loads must NOT be flagged by a loose
            // prefix: a detector that cries wolf here would be turned off and stop asserting anything.
            "System.Data.Common",
            "System.Net.Http",
            "PuddingCodeIntelligence",
            "PuddingRuntime.Services",
            "PuddingHost",
            "PuddingAgent",
            "PuddingIndexChunking",
            "PuddingFullTextIndex",
            "PuddingRetrievalEval.Contracts",
            "PuddingCodeIndex.Services",
        ]);

        CollectionAssert.AreEquivalent(
            new[]
            {
                "Lucene.Net",
                "Microsoft.Build.Locator",
                "Microsoft.CodeAnalysis.CSharp",
                "Microsoft.Data.Sqlite",
                "PuddingAgent",
                "PuddingCodeIndex.Services",
                "PuddingCodeIntelligence",
                "PuddingFullTextIndex",
                "PuddingHost",
                "PuddingIndexChunking",
                "PuddingRetrievalEval.Contracts",
                "PuddingRuntime.Services",
            },
            flagged.ToArray(),
            "the boundary detector must flag exactly the forbidden assembly names, and nothing else");

        Assert.IsEmpty(
            FindViolations(["PuddingVectorIndex", "PuddingVectorIndexTests", "MSTest.TestFramework"]),
            "the detector must not flag allowed assemblies");
    }

    /// <summary>Self-check of the source-token detector (positive control for the scan below).</summary>
    [TestMethod]
    public void Source_Token_Detector_Flags_Http_And_Secret_Tokens()
    {
        const string offending = """
            var client = new HttpClient();
            var url = "http://127.0.0.1:1234/v1";
            var key = apiKey;
            """;

        var flagged = FindForbiddenSourceTokens(offending);

        CollectionAssert.AreEquivalent(
            new[] { "HttpClient", "http://", "apiKey" },
            flagged.ToArray());

        Assert.IsEmpty(
            FindForbiddenSourceTokens("public sealed class InMemoryVectorIndex { }"),
            "clean source must not be flagged");
    }

    /// <summary>The test process must not have Roslyn / Lucene / any upper layer loaded.</summary>
    [TestMethod]
    public void Test_Process_Must_Not_Load_Forbidden_Assemblies()
    {
        var testAssembly = typeof(ComponentBoundaryTests).Assembly;
        Assert.AreEqual(
            "PuddingVectorIndexTests",
            testAssembly.GetName().Name,
            "boundary assertions must run inside the component test assembly");

        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetName().Name)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .ToArray();

        var referenced = testAssembly.GetReferencedAssemblies()
            .Select(a => a.Name)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
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
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .Where(n => declared.Contains(n, StringComparer.Ordinal))
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
            "control: the vector component itself must be in this process' closure");
        Assert.Contains(
            ComponentAssemblyName,
            reachable,
            "control: the vector component must be reachable through the probing path");

        var violations = FindViolations(observed);
        Assert.IsEmpty(
            violations,
            "the vector component must stay a leaf; loaded/declared upper layers: " + string.Join(", ", violations));
    }

    /// <summary>The component assembly must not reference an HTTP client or a database/Lucene library.</summary>
    [TestMethod]
    public void Component_Assembly_Must_Not_Reference_Http_Database_Or_Lucene()
    {
        var componentAssembly = typeof(InMemoryVectorIndex).Assembly;

        var referenced = componentAssembly
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .ToArray();

        Assert.Contains(
            "System.Runtime",
            referenced,
            "control: the component assembly must expose its reference table at all");

        var violations = FindViolations(referenced);
        Assert.IsEmpty(
            violations,
            "the component must not reference HTTP/database/upper layers, found: " + string.Join(", ", violations));
    }

    /// <summary>The component's sources must not contain HTTP or credential tokens.</summary>
    [TestMethod]
    public void Component_Sources_Must_Not_Mention_Http_Or_Credentials()
    {
        var projectDirectory = FindRepositoryFile(Path.Combine("Source", ComponentAssemblyName));
        Assert.IsTrue(Directory.Exists(projectDirectory), $"expected the component directory at {projectDirectory}");

        var sources = Directory.EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories).ToArray();
        Assert.IsTrue(sources.Length > 0, "control: the scan must actually have files to read");

        var offenders = new List<string>();
        foreach (var source in sources)
        {
            var flagged = FindForbiddenSourceTokens(File.ReadAllText(source));
            if (flagged.Count > 0)
                offenders.Add($"{Path.GetFileName(source)}: {string.Join(", ", flagged)}");
        }

        Assert.IsEmpty(
            offenders,
            "the component must not speak HTTP nor hold credentials; offenders: " + string.Join(" | ", offenders));
    }

    /// <summary>
    /// The component's own csproj must declare <c>ProjectReference = 0</c> and
    /// <c>PackageReference = 0</c> — the leaf manifest (task book A1).
    /// </summary>
    [TestMethod]
    public void Component_Csproj_Must_Declare_Zero_Project_And_Package_References()
    {
        var csprojPath = FindRepositoryFile(Path.Combine("Source", ComponentAssemblyName, ComponentAssemblyName + ".csproj"));
        Assert.IsTrue(File.Exists(csprojPath), $"expected the component csproj at {csprojPath}");

        var text = File.ReadAllText(csprojPath);

        Assert.IsFalse(
            text.Contains("<ProjectReference", StringComparison.OrdinalIgnoreCase),
            "the leaf component must declare no ProjectReference");
        Assert.IsFalse(
            text.Contains("<PackageReference", StringComparison.OrdinalIgnoreCase),
            "the leaf component must declare no PackageReference (no new NuGet, no vector database)");
        Assert.IsFalse(
            text.Contains("InternalsVisibleTo", StringComparison.OrdinalIgnoreCase),
            "the leaf component must not open itself to another assembly; public API only");
    }

    /// <summary>
    /// The test csproj must reference the component and nothing else (组件化交付规程 S2). This reads the
    /// file from disk rather than trusting the running assembly, so the manifest itself cannot drift.
    /// </summary>
    [TestMethod]
    public void Test_Csproj_Must_Reference_Only_The_Component()
    {
        var csprojPath = FindRepositoryFile(
            Path.Combine("Source", "PuddingVectorIndexTests", "PuddingVectorIndexTests.csproj"));
        Assert.IsTrue(File.Exists(csprojPath), $"expected the test csproj at {csprojPath}");

        var text = File.ReadAllText(csprojPath);
        var references = System.Text.RegularExpressions.Regex
            .Matches(text, @"<ProjectReference\s+Include=""([^""]+)""")
            .Select(match => match.Groups[1].Value.Replace('\\', '/'))
            .ToArray();

        CollectionAssert.AreEqual(
            new[] { "../PuddingVectorIndex/PuddingVectorIndex.csproj" },
            references,
            "the component test project must reference exactly one project: the component itself");
        Assert.IsFalse(
            text.Contains("<PackageReference", StringComparison.OrdinalIgnoreCase),
            "the test project must not add package references of its own");
    }

    private static bool IsForbidden(string assemblyName) =>
        ForbiddenAssemblyPrefixes.Any(p => assemblyName.StartsWith(p, StringComparison.Ordinal))
        || ForbiddenAssemblyNames.Any(
            upper => string.Equals(assemblyName, upper, StringComparison.Ordinal)
                     || assemblyName.StartsWith(upper + ".", StringComparison.Ordinal));

    private static List<string> FindViolations(IEnumerable<string> assemblyNames) =>
        assemblyNames
            .Where(IsForbidden)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    private static List<string> FindForbiddenSourceTokens(string text) =>
        ForbiddenSourceTokens
            .Where(token => text.Contains(token, StringComparison.OrdinalIgnoreCase))
            .OrderBy(token => token, StringComparer.Ordinal)
            .ToList();

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

        Assert.Fail("could not locate the repository root (PuddingAgentNetwork.slnx) above " + AppContext.BaseDirectory);
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
            foreach (var library in libraries.EnumerateObject())
                names.Add(StripVersion(library.Name));

        if (document.RootElement.TryGetProperty("targets", out var targets))
            foreach (var target in targets.EnumerateObject())
                foreach (var library in target.Value.EnumerateObject())
                    names.Add(StripVersion(library.Name));

        return names.ToArray();
    }

    private static string StripVersion(string libraryKey)
    {
        var separator = libraryKey.IndexOf('/');
        return separator < 0 ? libraryKey : libraryKey[..separator];
    }
}
