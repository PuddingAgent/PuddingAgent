using System.Diagnostics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using PuddingCodeIntelligence.Extractors;

namespace PuddingCodeIntelligenceTests.Extractors;

/// <summary>
/// B4+ A5 / M2: NODE_PATH has to be scoped to the extraction child process
/// (ProcessStartInfo.Environment) and must never be set process-wide
/// (Environment.SetEnvironmentVariable leaks into every later subprocess of the host).
/// </summary>
[TestClass]
public sealed class ExtractorSubprocessEnvironmentTests
{
    [TestMethod]
    public void ApplyNodeModulesPath_SetsChildEnvironmentOnly()
    {
        var nodeModulesPath = Path.Combine(Path.GetTempPath(), "pudding-extractor-assets", "node_modules");
        var nodePathBefore = Environment.GetEnvironmentVariable("NODE_PATH") ?? "<unset>";

        var startInfo = new ProcessStartInfo { FileName = "node" };

        ExtractorSubprocessEnvironment.ApplyNodeModulesPath(startInfo, nodeModulesPath);

        Assert.AreEqual(nodeModulesPath, startInfo.Environment["NODE_PATH"],
            "the child process environment must carry NODE_PATH");
        Assert.AreEqual(nodePathBefore, Environment.GetEnvironmentVariable("NODE_PATH") ?? "<unset>",
            "A5/M2 failed: the host process NODE_PATH was mutated");
    }

    [TestMethod]
    public void ApplyNodeModulesPath_NullPath_LeavesChildEnvironmentUnchanged()
    {
        var expected = Environment.GetEnvironmentVariable("NODE_PATH");
        var startInfo = new ProcessStartInfo { FileName = "python" };

        ExtractorSubprocessEnvironment.ApplyNodeModulesPath(startInfo, null);

        startInfo.Environment.TryGetValue("NODE_PATH", out var actual);
        Assert.AreEqual(expected, actual,
            "a kind that needs no node modules must not add NODE_PATH to the child environment");
    }
}
