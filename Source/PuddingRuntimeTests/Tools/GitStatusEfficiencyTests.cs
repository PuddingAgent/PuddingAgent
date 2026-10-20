using System.Text.Json;
using LibGit2Sharp;
using PuddingCode.Tools;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

[TestClass]
public sealed class GitStatusEfficiencyTests
{
    [TestMethod]
    public async Task Status_Excludes_Ignored_Noise_But_Preserves_Changes_And_OptIn()
    {
        var root = Path.Combine(Path.GetTempPath(), "pudding-git-status", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Repository.Init(root);
            using (var repo = new Repository(root))
            {
                File.WriteAllText(Path.Combine(root, ".gitignore"), "cache/\n");
                File.WriteAllText(Path.Combine(root, "tracked.txt"), "original");
                Commands.Stage(repo, new[] { ".gitignore", "tracked.txt" });
                var author = new Signature("test", "test@example.invalid", DateTimeOffset.UtcNow);
                repo.Commit("fixture", author, author);
            }
            Directory.CreateDirectory(Path.Combine(root, "cache"));
            File.WriteAllText(Path.Combine(root, "cache", "ignored.txt"), "noise");
            using (var clean = await Status(root, false))
            {
                Assert.IsFalse(clean.RootElement.GetProperty("has_changes").GetBoolean());
                Assert.AreEqual(0, clean.RootElement.GetProperty("entries").GetArrayLength());
            }
            File.WriteAllText(Path.Combine(root, "tracked.txt"), "modified");
            File.WriteAllText(Path.Combine(root, "new.txt"), "untracked");
            using (var changed = await Status(root, false))
            {
                Assert.IsTrue(changed.RootElement.GetProperty("has_changes").GetBoolean());
                var entries = changed.RootElement.GetProperty("entries").EnumerateArray().ToArray();
                CollectionAssert.AreEquivalent(new[] { "tracked.txt", "new.txt" }, entries.Select(e => e.GetProperty("path").GetString()).ToArray());
            }
            using var explicitIgnored = await Status(root, true);
            Assert.IsTrue(explicitIgnored.RootElement.GetProperty("entries").EnumerateArray()
                .Any(e => e.GetProperty("state").GetString() == "Ignored"));
        }
        finally
        {
            // Git loose objects can be read-only on Windows; cleanup must not mask an assertion.
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<JsonDocument> Status(string root, bool ignored)
    {
        var result = await new GitStatusTool().ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "status", ArgumentsJson = JsonSerializer.Serialize(new { path = root, include_ignored = ignored }),
            Context = new ToolExecutionContext { SessionId = "test", WorkspaceId = "test", AgentInstanceId = "test" },
        });
        Assert.IsTrue(result.Success, result.Error);
        return JsonDocument.Parse(result.Output);
    }
}
