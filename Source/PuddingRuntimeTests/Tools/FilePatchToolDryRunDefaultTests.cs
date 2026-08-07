using System.Text;
using System.Text.Json;
using PuddingCode.Tools;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// Verifies the dry_run default inversion (plan A):
/// dry_run omitted or false → changes are applied directly;
/// dry_run=true → preview only, no writes.
/// Covers both paths: operations-based patching and unified-diff patching
/// (file_patch patch_text + apply_patch share UnifiedDiffPatchRunner).
/// </summary>
[TestClass]
public sealed class FilePatchToolDryRunDefaultTests
{
    private string _tempDir = null!;
    private string _originalRepoRoot = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"pudding-fpt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _originalRepoRoot = Environment.GetEnvironmentVariable("PUDDING_REPOSITORY_ROOT") ?? string.Empty;
        Environment.SetEnvironmentVariable("PUDDING_REPOSITORY_ROOT", _tempDir);
        HostFileToolPaths.InvalidateWorkspaceRootCache();
    }

    [TestCleanup]
    public void Cleanup()
    {
        Environment.SetEnvironmentVariable("PUDDING_REPOSITORY_ROOT", _originalRepoRoot);
        HostFileToolPaths.InvalidateWorkspaceRootCache();
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    // ── Path 1: operations-based patching ──

    // Test 1: dry_run omitted → file is actually modified (new default behavior)
    [TestMethod]
    public async Task OperationsPath_DryRunOmitted_AppliesChangesDirectly()
    {
        var filePath = Path.Combine(_tempDir, "sample.txt");
        File.WriteAllText(filePath, "hello old world\n", Encoding.UTF8);

        var tool = CreateFilePatchTool();
        var result = await ExecuteFilePatchAsync(tool, new Dictionary<string, object?>
        {
            ["path"] = "sample.txt",
            ["operations"] = new object[]
            {
                new Dictionary<string, object?> { ["type"] = "replace", ["old_text"] = "old", ["new_text"] = "new" }
            }
        });

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual("hello new world\n", File.ReadAllText(filePath, Encoding.UTF8));
        StringAssert.Contains(result.Output, "patched");
        Assert.IsFalse(result.Output.Contains("preview"), "applied result must not be labeled as preview");
    }

    // Test 2: dry_run=true → preview only, file untouched
    [TestMethod]
    public async Task OperationsPath_DryRunTrue_PreviewsWithoutWriting()
    {
        var filePath = Path.Combine(_tempDir, "sample.txt");
        var original = "hello old world\n";
        File.WriteAllText(filePath, original, Encoding.UTF8);

        var tool = CreateFilePatchTool();
        var result = await ExecuteFilePatchAsync(tool, new Dictionary<string, object?>
        {
            ["path"] = "sample.txt",
            ["dry_run"] = true,
            ["operations"] = new object[]
            {
                new Dictionary<string, object?> { ["type"] = "replace", ["old_text"] = "old", ["new_text"] = "new" }
            }
        });

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(original, File.ReadAllText(filePath, Encoding.UTF8), "dry-run must not modify the file");
        StringAssert.Contains(result.Output, "no changes written");
    }

    // ── Path 2: unified diff (shared by file_patch patch_text and apply_patch) ──

    // Test 3: file_patch with patch_text, dry_run omitted → applied directly
    [TestMethod]
    public async Task UnifiedDiffPath_DryRunOmitted_AppliesChangesDirectly()
    {
        var filePath = Path.Combine(_tempDir, "sample.txt");
        File.WriteAllText(filePath, "hello old world\n", Encoding.UTF8);

        var tool = CreateFilePatchTool();
        var result = await ExecuteFilePatchAsync(tool, new Dictionary<string, object?>
        {
            ["patch_text"] = BuildDiff()
        });

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual("hello new world\n", File.ReadAllText(filePath, Encoding.UTF8));
        StringAssert.Contains(result.Output, "patched");
    }

    // Test 4: apply_patch tool, dry_run omitted → applied directly
    [TestMethod]
    public async Task ApplyPatchTool_DryRunOmitted_AppliesChangesDirectly()
    {
        var filePath = Path.Combine(_tempDir, "sample.txt");
        File.WriteAllText(filePath, "hello old world\n", Encoding.UTF8);

        var tool = CreateApplyPatchTool();
        var result = await ExecuteApplyPatchAsync(tool, new Dictionary<string, object?>
        {
            ["patch_text"] = BuildDiff()
        });

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual("hello new world\n", File.ReadAllText(filePath, Encoding.UTF8));
        StringAssert.Contains(result.Output, "patched");
    }

    // Test 5: apply_patch tool, dry_run=true → preview only, file untouched
    [TestMethod]
    public async Task ApplyPatchTool_DryRunTrue_PreviewsWithoutWriting()
    {
        var filePath = Path.Combine(_tempDir, "sample.txt");
        var original = "hello old world\n";
        File.WriteAllText(filePath, original, Encoding.UTF8);

        var tool = CreateApplyPatchTool();
        var result = await ExecuteApplyPatchAsync(tool, new Dictionary<string, object?>
        {
            ["patch_text"] = BuildDiff(),
            ["dry_run"] = true
        });

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(original, File.ReadAllText(filePath, Encoding.UTF8), "dry-run must not modify the file");
        StringAssert.Contains(result.Output, "no changes written");
    }

    // ── Helpers ──

    private static string BuildDiff() =>
        "--- a/sample.txt\n+++ b/sample.txt\n@@ -1 +1 @@\n-hello old world\n+hello new world\n";

    // Parameterless constructors resolve data paths from HostFileToolPaths.WorkspaceRoot,
    // which the test redirects to a temp dir via PUDDING_REPOSITORY_ROOT.
    private static FilePatchTool CreateFilePatchTool() => new();

    private static ApplyPatchTool CreateApplyPatchTool() => new();

    private static Task<ToolExecutionResult> ExecuteFilePatchAsync(
        FilePatchTool tool, IReadOnlyDictionary<string, object?> parameters)
        => ExecuteAsync(tool, parameters);

    private static Task<ToolExecutionResult> ExecuteApplyPatchAsync(
        ApplyPatchTool tool, IReadOnlyDictionary<string, object?> parameters)
        => ExecuteAsync(tool, parameters);

    private static Task<ToolExecutionResult> ExecuteAsync<TArgs>(
        PuddingToolBase<TArgs> tool, IReadOnlyDictionary<string, object?> parameters)
        where TArgs : class
    {
        return tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "call-1",
            ArgumentsJson = JsonSerializer.Serialize(parameters),
            Context = new ToolExecutionContext
            {
                AgentInstanceId = "agent",
                WorkspaceId = "workspace",
                SessionId = "session",
            },
        });
    }
}
