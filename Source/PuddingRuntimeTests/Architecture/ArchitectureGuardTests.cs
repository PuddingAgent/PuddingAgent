using System.Xml.Linq;

namespace PuddingRuntimeTests.Architecture;

/// <summary>
/// Guards that PuddingRuntime does not depend on PuddingPlatform (reverse dependency).
/// This test is expected to FAIL initially; it will PASS after Batch 3 refactoring removes the reference.
/// </summary>
[TestClass]
public sealed class ArchitectureGuardTests
{
    [TestMethod]
    public void PuddingRuntime_MustNot_Reference_PuddingPlatform()
    {
        var runtimeCsprojPath = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
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
        var runtimeSourcePath = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "Source", "PuddingRuntime");

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
}
