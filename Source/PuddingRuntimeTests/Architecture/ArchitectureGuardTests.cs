using System.Xml.Linq;

namespace PuddingRuntimeTests.Architecture;

/// <summary>
/// Guards that PuddingRuntime does not depend on PuddingPlatform (reverse dependency).
/// </summary>
[TestClass]
public sealed class ArchitectureGuardTests
{
    [TestMethod]
    public void PuddingRuntime_MustNot_Reference_PuddingPlatform()
    {
        var runtimeCsprojPath = Path.Combine(
            FindRepositoryRoot(),
            "Source", "PuddingRuntime", "PuddingRuntime.csproj");

        var fullPath = Path.GetFullPath(runtimeCsprojPath);
        Assert.IsTrue(File.Exists(fullPath), $"Expected PuddingRuntime.csproj at: {fullPath}");

        var doc = XDocument.Load(fullPath);
        var platformRefs = doc.Descendants("ProjectReference")
            .Where(r => r.Attribute("Include")?.Value.Contains("PuddingPlatform", StringComparison.OrdinalIgnoreCase) == true)
            .ToList();

        Assert.AreEqual(0, platformRefs.Count,
            "PuddingRuntime must not reference PuddingPlatform. " +
            $"Found {platformRefs.Count} reference(s). " +
            "Move required contracts/DTOs to PuddingCore instead.");
    }

    [TestMethod]
    public void PuddingRuntime_MustNot_Contain_UsingPuddingPlatform()
    {
        var runtimeSourcePath = Path.Combine(FindRepositoryRoot(), "Source", "PuddingRuntime");

        var fullPath = Path.GetFullPath(runtimeSourcePath);
        Assert.IsTrue(Directory.Exists(fullPath), $"Expected PuddingRuntime directory at: {fullPath}");

        var csFiles = Directory.GetFiles(fullPath, "*.cs", SearchOption.AllDirectories);
        var violations = new List<string>();

        foreach (var file in csFiles)
        {
            var content = File.ReadAllText(file);
            if (content.Contains("using PuddingPlatform", StringComparison.Ordinal))
            {
                violations.Add(file);
            }
        }

        Assert.AreEqual(0, violations.Count,
            $"PuddingRuntime source files must not contain 'using PuddingPlatform'. " +
            $"Found {violations.Count} violation(s):\n" +
            string.Join("\n", violations.Select(v => $"  - {Path.GetFileName(v)}")));
    }

    /// <summary>
    /// Locates the repository root by walking up from the test output directory until the
    /// solution file is found.
    /// <para>
    /// The previous implementation hard-coded five <c>..</c> segments, which only holds for the
    /// default <c>bin/Debug/net10.0</c> layout. Building with <c>-p:ArtifactsPath</c> (needed on
    /// this machine to avoid file locks held by the running Core) inserts one extra directory, so
    /// both guards failed with "repository root not found" — the assertion never actually ran, and
    /// the red was about the harness rather than the architecture. Walking up to a known marker
    /// keeps the guard honest under any output layout.
    /// </para>
    /// </summary>
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
            $"Cannot locate the repository root (PuddingAgentNetwork.slnx) by walking up from '{AppContext.BaseDirectory}'.");
    }
}
