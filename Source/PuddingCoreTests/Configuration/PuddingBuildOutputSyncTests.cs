using PuddingCode.Configuration;

namespace PuddingCoreTests.Configuration;

[TestClass]
public sealed class PuddingBuildOutputSyncTests
{
    [TestMethod]
    public void SyncDirectory_CopiesNewFiles_And_Counts()
    {
        using var temp = new TestDirectory();

        var source = temp.CreateDirectory("src");
        File.WriteAllText(Path.Combine(source, "a.txt"), "aaa");
        File.WriteAllText(Path.Combine(source, "b.txt"), "bbb");
        Directory.CreateDirectory(Path.Combine(source, "wwwroot"));
        File.WriteAllText(Path.Combine(source, "wwwroot", "index.html"), "<html/>");

        var target = temp.CreateDirectory("dst");

        var result = PuddingBuildOutputSync.SyncDirectory(source, target);

        Assert.AreEqual(3, result.Copied);
        Assert.AreEqual(0, result.Skipped);
        Assert.IsTrue(result.Success);
        Assert.AreEqual("aaa", File.ReadAllText(Path.Combine(target, "a.txt")));
        Assert.AreEqual("<html/>", File.ReadAllText(Path.Combine(target, "wwwroot", "index.html")));
    }

    [TestMethod]
    public void SyncDirectory_Overwrites_Identical_Files_Are_Skipped()
    {
        using var temp = new TestDirectory();

        var source = temp.CreateDirectory("src");
        File.WriteAllText(Path.Combine(source, "same.txt"), "same");
        File.WriteAllText(Path.Combine(source, "changed.txt"), "new-content");

        var target = temp.CreateDirectory("dst");
        File.WriteAllText(Path.Combine(target, "same.txt"), "same");
        File.WriteAllText(Path.Combine(target, "changed.txt"), "old-content");

        var result = PuddingBuildOutputSync.SyncDirectory(source, target);

        Assert.AreEqual(1, result.Copied);   // changed.txt overwritten
        Assert.AreEqual(1, result.Skipped);  // same.txt byte-identical
        Assert.IsTrue(result.Success);
        Assert.AreEqual("new-content", File.ReadAllText(Path.Combine(target, "changed.txt")));
        Assert.AreEqual("same", File.ReadAllText(Path.Combine(target, "same.txt")));
    }

    [TestMethod]
    public void SyncDirectory_MissingSource_Reports_Failure()
    {
        using var temp = new TestDirectory();
        var target = temp.CreateDirectory("dst");

        var result = PuddingBuildOutputSync.SyncDirectory(
            Path.Combine(temp.Root, "does-not-exist"), target);

        Assert.AreEqual(0, result.Copied);
        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.Failures.Count > 0);
    }

    [TestMethod]
    public void SyncDirectory_FailedFile_Does_Not_Block_Others()
    {
        using var temp = new TestDirectory();

        var source = temp.CreateDirectory("src");
        File.WriteAllText(Path.Combine(source, "good.txt"), "good");
        File.WriteAllText(Path.Combine(source, "bad.txt"), "bad");

        // Target "directory" is actually a file → copies into it must fail.
        var targetPath = Path.Combine(temp.Root, "dst");
        File.WriteAllText(targetPath, "i am a file, not a directory");
        var target = targetPath;

        var result = PuddingBuildOutputSync.SyncDirectory(source, target);

        Assert.AreEqual(0, result.Copied);
        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.Failures.Count > 0);
    }

    [TestMethod]
    public void TryParseBuildOutputDirectory_Extracts_AbsolutePath()
    {
        var log = """
            Determining projects to restore...
            Restored E:\repo\Source\PuddingAgent\PuddingAgent.csproj
            PuddingCore -> E:\repo\Source\PuddingCore\bin\Debug\net10.0\
            PuddingAgent -> E:\repo\Source\PuddingAgent\bin\Debug\net10.0\
            Build succeeded.
            """;

        var output = PuddingBuildOutputSync.TryParseBuildOutputDirectory(log, "PuddingAgent");

        Assert.AreEqual(@"E:\repo\Source\PuddingAgent\bin\Debug\net10.0", output);
    }

    [TestMethod]
    public void TryParseBuildOutputDirectory_TakesLastMatch()
    {
        var log = """
            PuddingAgent -> E:\repo\out\first\
            PuddingAgent -> E:\repo\out\second\
            """;

        var output = PuddingBuildOutputSync.TryParseBuildOutputDirectory(log, "PuddingAgent");

        Assert.AreEqual(@"E:\repo\out\second", output);
    }

    [TestMethod]
    public void TryParseBuildOutputDirectory_TargetDll_ReturnsContainingDirectory()
    {
        var log = "  PuddingAgent -> E:\\repo\\Source\\PuddingAgent\\bin\\Debug\\net10.0\\PuddingAgent.dll";

        var output = PuddingBuildOutputSync.TryParseBuildOutputDirectory(log, "PuddingAgent");

        Assert.AreEqual(@"E:\repo\Source\PuddingAgent\bin\Debug\net10.0", output);
    }

    [TestMethod]
    public void TryParseBuildOutputDirectory_ReturnsNull_WhenProjectAbsent()
    {
        var log = """
            PuddingCore -> E:\repo\Source\PuddingCore\bin\Debug\net10.0\
            Build succeeded.
            """;

        Assert.IsNull(PuddingBuildOutputSync.TryParseBuildOutputDirectory(log, "PuddingAgent"));
        Assert.IsNull(PuddingBuildOutputSync.TryParseBuildOutputDirectory(null, "PuddingAgent"));
        Assert.IsNull(PuddingBuildOutputSync.TryParseBuildOutputDirectory("", "PuddingAgent"));
    }

    [TestMethod]
    public void DeployDirectoryTransactional_DeploysChangedFiles_AndVerifiesHash()
    {
        using var temp = new TestDirectory();
        var source = temp.CreateDirectory("artifact");
        var target = temp.CreateDirectory("runtime");
        File.WriteAllText(Path.Combine(source, "PuddingAgent.dll"), "new-assembly");
        File.WriteAllText(Path.Combine(source, "unchanged.json"), "same");
        File.WriteAllText(Path.Combine(target, "PuddingAgent.dll"), "old-assembly");
        File.WriteAllText(Path.Combine(target, "unchanged.json"), "same");

        var result = PuddingBuildOutputSync.DeployDirectoryTransactional(source, target);

        Assert.IsTrue(result.Success);
        Assert.AreEqual(1, result.Copied);
        Assert.AreEqual(1, result.Skipped);
        Assert.AreEqual("new-assembly", File.ReadAllText(Path.Combine(target, "PuddingAgent.dll")));
        Assert.AreEqual(
            PuddingBuildOutputSync.ComputeSha256(Path.Combine(source, "PuddingAgent.dll")),
            PuddingBuildOutputSync.ComputeSha256(Path.Combine(target, "PuddingAgent.dll")));
    }

    [TestMethod]
    public void DeployDirectoryTransactional_SameDirectory_IsVerifiedNoOp()
    {
        using var temp = new TestDirectory();
        var source = temp.CreateDirectory("artifact");
        File.WriteAllText(Path.Combine(source, "PuddingAgent.dll"), "assembly");

        var result = PuddingBuildOutputSync.DeployDirectoryTransactional(source, source);

        Assert.IsTrue(result.Success);
        Assert.AreEqual(0, result.Copied);
        Assert.AreEqual(1, result.Skipped);
    }

    [TestMethod]
    public void IsPathWithin_RejectsSiblingPrefix()
    {
        using var temp = new TestDirectory();
        var root = temp.CreateDirectory("repo");
        var child = temp.CreateDirectory(Path.Combine("repo", "out"));
        var sibling = temp.CreateDirectory("repository-other");

        Assert.IsTrue(PuddingBuildOutputSync.IsPathWithin(child, root));
        Assert.IsTrue(PuddingBuildOutputSync.IsPathWithin(root, root));
        Assert.IsFalse(PuddingBuildOutputSync.IsPathWithin(sibling, root));
    }

    [TestMethod]
    public void ComputeManagedArtifactManifest_CoversDependentAssemblies_AndIgnoresStaleExtras()
    {
        using var temp = new TestDirectory();
        var prepared = temp.CreateDirectory("prepared");
        var loaded = temp.CreateDirectory("loaded");
        File.WriteAllText(Path.Combine(prepared, "PuddingAgent.dll"), "entry");
        File.WriteAllText(Path.Combine(prepared, "PuddingRuntime.dll"), "tool-v2");
        File.WriteAllText(Path.Combine(prepared, "PuddingAgent.runtimeconfig.json"), "{}");

        PuddingBuildOutputSync.SyncDirectory(prepared, loaded);
        File.WriteAllText(Path.Combine(loaded, "stale.dll"), "ignored because it is not in the prepared set");

        var preparedManifest = PuddingBuildOutputSync.ComputeManagedArtifactManifest(prepared);
        var loadedManifest = PuddingBuildOutputSync.ComputeManagedArtifactManifest(
            loaded,
            preparedManifest.RelativePaths);

        Assert.AreEqual(3, preparedManifest.FileCount);
        Assert.AreEqual(preparedManifest.Sha256, loadedManifest.Sha256);

        File.WriteAllText(Path.Combine(loaded, "PuddingRuntime.dll"), "tool-v1");
        var staleRuntimeManifest = PuddingBuildOutputSync.ComputeManagedArtifactManifest(
            loaded,
            preparedManifest.RelativePaths);
        Assert.AreNotEqual(preparedManifest.Sha256, staleRuntimeManifest.Sha256);
    }

    /// <summary>Minimal temp-directory helper with recursive cleanup (test-local).</summary>
    private sealed class TestDirectory : IDisposable
    {
        public string Root { get; } = Path.Combine(
            Path.GetTempPath(), $"pudding-bos-{Guid.NewGuid():N}");

        public TestDirectory() => Directory.CreateDirectory(Root);

        public string CreateDirectory(string relative)
        {
            var path = Path.Combine(Root, relative);
            Directory.CreateDirectory(path);
            return path;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                    Directory.Delete(Root, recursive: true);
            }
            catch
            {
                // Best-effort cleanup only.
            }
        }
    }
}
