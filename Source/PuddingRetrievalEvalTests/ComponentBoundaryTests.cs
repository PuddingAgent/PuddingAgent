using System.Reflection;
using System.Text.Json;

namespace PuddingRetrievalEvalTests;

/// <summary>
/// S3/S4 boundary assertions for the evaluation component (组件化交付规程 §3 S3/S4, ADR-089 §6).
/// <para>
/// The value of <c>PuddingRetrievalEval</c> is that it is a leaf: it can be built and tested with no
/// host, and it observes retrieval engines only through <c>ISearchProbe</c>. If this component ever
/// referenced a retrieval engine (or a host layer), that design would be gone — so the boundary is
/// asserted here and the detector itself is checked against a positive control.
/// </para>
/// </summary>
[TestClass]
public sealed class ComponentBoundaryTests
{
    /// <summary>Exact upper-layer / engine assembly names this test process must never depend on.</summary>
    private static readonly string[] ForbiddenAssemblyNames =
    [
        "PuddingAgent",
        "PuddingCodeIndex",
        "PuddingCodeIntelligence",
        "PuddingFullTextIndex",
        "PuddingHost",
        "PuddingPlatform",
        "PuddingRuntime",
    ];

    /// <summary>Assembly-name prefixes of the deliberately isolated heavy dependencies.</summary>
    private static readonly string[] ForbiddenAssemblyPrefixes =
    [
        "Lucene.Net",
        "Microsoft.Build",
        "Microsoft.CodeAnalysis",
    ];

    private const string ComponentAssemblyName = "PuddingRetrievalEval";

    /// <summary>
    /// Positive control for the detector itself: a green assertion is worthless if the filter cannot
    /// report a violation. Without this, "0 violations" could just mean "broken detector".
    /// </summary>
    [TestMethod]
    public void Boundary_Detector_Flags_Forbidden_Names()
    {
        var flagged = FindViolations(
        [
            ComponentAssemblyName,
            "PuddingRetrievalEvalTests",
            "MSTest.TestFramework",
            "PuddingAgent",
            "PuddingCodeIndex",
            "PuddingCodeIntelligence",
            "PuddingFullTextIndex",
            "PuddingHost",
            "PuddingPlatform",
            "PuddingRuntime",
            "Microsoft.CodeAnalysis.CSharp",
            "Microsoft.Build.Locator",
            "Lucene.Net.Search",
            "Lucene.Net.Store",
        ]);

        CollectionAssert.AreEquivalent(
            new[]
            {
                "Lucene.Net.Search",
                "Lucene.Net.Store",
                "Microsoft.Build.Locator",
                "Microsoft.CodeAnalysis.CSharp",
                "PuddingAgent",
                "PuddingCodeIndex",
                "PuddingCodeIntelligence",
                "PuddingFullTextIndex",
                "PuddingHost",
                "PuddingPlatform",
                "PuddingRuntime",
            },
            flagged.ToArray(),
            "the boundary detector must flag exactly the forbidden assembly names, and nothing else");

        Assert.IsEmpty(
            FindViolations([ComponentAssemblyName, "PuddingRetrievalEvalTests", "MSTest.TestFramework", "System.Text.Json"]),
            "the detector must not flag allowed assemblies");
    }

    /// <summary>
    /// This test process must not load an engine, a host layer or a heavy parser. Metadata references are
    /// force-loaded first so the assertion cannot be defeated by the CLR's lazy assembly loading.
    /// </summary>
    [TestMethod]
    public void Test_Process_Must_Not_Load_Forbidden_Assemblies()
    {
        var testAssembly = typeof(ComponentBoundaryTests).Assembly;
        Assert.AreEqual(
            "PuddingRetrievalEvalTests",
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

        var forceLoaded = new List<string>();
        foreach (var name in referenced)
        {
            try
            {
                forceLoaded.Add(Assembly.Load(name)!.GetName().Name!);
            }
            catch (Exception)
            {
                // Unresolvable references are irrelevant here; the assertion is about *forbidden* names.
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

        Assert.Contains(
            ComponentAssemblyName,
            observed,
            "control: the evaluation component itself must be in this process' closure, otherwise the check is vacuous");
        Assert.Contains(
            ComponentAssemblyName,
            reachable,
            "control: the evaluation component must be reachable through the probing path");

        var violations = FindViolations(observed);

        Assert.AreEqual(
            0,
            violations.Count,
            "the evaluation component must stay a leaf; forbidden assemblies observed: " + string.Join(", ", violations));

        // Independent assertion on the declared dependency closure, not just on what happens to be loaded.
        var closureViolations = declared.Where(IsForbidden).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.AreEqual(
            0,
            closureViolations.Length,
            "the dependency closure declared in deps.json must contain no forbidden assembly: "
            + string.Join(", ", closureViolations));
    }

    private static List<string> FindViolations(IEnumerable<string> assemblyNames) =>
        assemblyNames.Where(IsForbidden).OrderBy(n => n, StringComparer.Ordinal).ToList();

    private static bool IsForbidden(string assemblyName) =>
        ForbiddenAssemblyPrefixes.Any(p => assemblyName.StartsWith(p, StringComparison.Ordinal))
        || ForbiddenAssemblyNames.Any(
            upper => string.Equals(assemblyName, upper, StringComparison.Ordinal)
                     || assemblyName.StartsWith(upper + ".", StringComparison.Ordinal));

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
