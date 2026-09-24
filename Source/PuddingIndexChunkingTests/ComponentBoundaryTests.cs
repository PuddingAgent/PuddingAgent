using System.Reflection;
using System.Text.Json;
using PuddingIndexChunking;

namespace PuddingIndexChunkingTests;

/// <summary>
/// S3/S4 boundary assertions for the chunking component (组件化交付规程 §3 S3/S4).
/// <para>
/// The promise of <c>PuddingIndexChunking</c> is that it is a <b>leaf</b>: chunking + filtering can be
/// built and tested with no Roslyn, no MSBuild and no upper layer present, because the outline arrives
/// through a port. A green claim of "no upper layer is loaded" is worthless without a detector that can
/// report a violation, so <c>Detector_Flags_Forbidden_Names</c> is the positive control and the
/// csproj-manifest test is the mechanical one.
/// </para>
/// </summary>
[TestClass]
public sealed class ComponentBoundaryTests
{
    private static readonly string[] ForbiddenAssemblyNames =
    [
        "PuddingCodeIntelligence",
        "PuddingCodeIndex",
        "PuddingRuntime",
        "PuddingHost",
        "PuddingAgent",
        "PuddingRetrievalEval",
        "PuddingFullTextIndex",
    ];

    private static readonly string[] ForbiddenAssemblyPrefixes =
    [
        "Microsoft.CodeAnalysis",
        "Microsoft.Build",
    ];

    private const string ComponentAssemblyName = "PuddingIndexChunking";

    /// <summary>Self-check of the detector: it must flag exactly the forbidden names, and nothing else.</summary>
    [TestMethod]
    public void Detector_Flags_Forbidden_Names()
    {
        var flagged = FindViolations(
        [
            "PuddingIndexChunking",
            "PuddingIndexChunkingTests",
            "MSTest.TestFramework",
            "Microsoft.CodeAnalysis.CSharp",
            "Microsoft.Build.Locator",
            "PuddingCodeIntelligence",
            "PuddingCodeIndex.Services",
            "PuddingRuntime.Services",
            "PuddingHost",
            "PuddingAgent",
            "PuddingRetrievalEval.Contracts",
            "PuddingFullTextIndex",
        ]);

        CollectionAssert.AreEquivalent(
            new[]
            {
                "Microsoft.Build.Locator",
                "Microsoft.CodeAnalysis.CSharp",
                "PuddingCodeIntelligence",
                "PuddingCodeIndex.Services",
                "PuddingFullTextIndex",
                "PuddingHost",
                "PuddingRetrievalEval.Contracts",
                "PuddingRuntime.Services",
                "PuddingAgent",
            },
            flagged.ToArray(),
            "the boundary detector must flag exactly the forbidden assembly names, and nothing else");

        Assert.IsEmpty(
            FindViolations(["PuddingIndexChunking", "PuddingIndexChunkingTests", "MSTest.TestFramework"]),
            "the detector must not flag allowed assemblies");
    }

    /// <summary>The test process must not have Roslyn / MSBuild / any upper layer loaded.</summary>
    [TestMethod]
    public void Test_Process_Must_Not_Load_Forbidden_Assemblies()
    {
        var testAssembly = typeof(ComponentBoundaryTests).Assembly;
        Assert.AreEqual(
            "PuddingIndexChunkingTests",
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

        // Force-load the metadata references: the CLR loads lazily, so an AppDomain-only check can
        // stay green while a forbidden reference sits right there in the manifest.
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
            "control: the chunking component itself must be in this process' closure");
        Assert.Contains(
            ComponentAssemblyName,
            reachable,
            "control: the chunking component must be reachable through the probing path");

        var violations = FindViolations(observed);
        Assert.IsEmpty(
            violations,
            "the chunking component must stay a leaf; loaded/declared upper layers: " + string.Join(", ", violations));
    }

    /// <summary>
    /// The component's own csproj must declare <c>ProjectReference = 0</c> and
    /// <c>PackageReference = 0</c> (the leaf manifest), and must keep its <c>InternalsVisibleTo</c>
    /// pointing at its own test project rather than at an upper layer (which would be a reverse
    /// dependency wearing a disguise).
    /// </summary>
    [TestMethod]
    public void Component_Manifest_Must_Stay_Leaf()
    {
        var csproj = Path.Combine(
            FindRepositoryRoot(), "Source", "PuddingIndexChunking", "PuddingIndexChunking.csproj");

        Assert.IsTrue(File.Exists(csproj), $"expected the component manifest at {csproj}");
        var text = File.ReadAllText(csproj);

        Assert.AreEqual(0, CountOccurrences(text, "<ProjectReference"), "a leaf component declares no ProjectReference");
        Assert.AreEqual(0, CountOccurrences(text, "<PackageReference"), "a leaf component declares no PackageReference");
        Assert.AreEqual(
            1,
            CountOccurrences(text, "<InternalsVisibleTo Include=\"PuddingIndexChunkingTests\""),
            "the component opens its internals to its own test project only");
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = 0;
        while (true)
        {
            index = text.IndexOf(needle, index, StringComparison.Ordinal);
            if (index < 0)
                return count;

            count++;
            index += needle.Length;
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PuddingAgentNetwork.slnx")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "could not locate the repository root (PuddingAgentNetwork.slnx) above " + AppContext.BaseDirectory);
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
