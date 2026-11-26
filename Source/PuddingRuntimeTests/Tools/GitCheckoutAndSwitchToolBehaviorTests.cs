using System.Text.Json;
using LibGit2Sharp;
using PuddingCode.Tools;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// git_checkout / git_branch_switch 的回执一致性（2026-11-26，第七处）。
///
/// 两个工具都声明了「会分离 HEAD」的合法路径，却都用 <c>repo.Head.FriendlyName</c> 回报，于是：
/// <list type="bullet">
/// <item>git_checkout 检出 tag/commit —— 工具描述里明写的常规用法 —— 回执 <c>current_branch</c> 是 <c>(no branch)</c>；</item>
/// <item>git_branch_switch 的 <c>Branches[...]</c> 会匹配远端跟踪分支，检出它同样分离 HEAD，
/// 回执于是同时给出 <c>name="(no branch)"</c> 与 <c>switched=true</c> —— 两句话互相矛盾。</item>
/// </list>
/// 每条「游离」用例都配一条「在分支上」的对照，避免用「无条件返回 detached …」的实现蒙过去。
/// </summary>
[TestClass]
public sealed class GitCheckoutAndSwitchToolBehaviorTests
{
    [TestMethod]
    public async Task Checkout_Tag_ReportsWhereHeadLanded_NotPlaceholder()
    {
        var fixture = new Fixture();
        try
        {
            using var doc = await ExecuteCheckout(fixture.Root, "v1");
            var result = doc.RootElement;

            Assert.AreEqual("v1", result.GetProperty("target").GetString());
            var branch = result.GetProperty("current_branch").GetString();
            Assert.AreEqual($"detached at {fixture.FirstCommitSha.Substring(0, 7)}", branch,
                "检出 tag 后 HEAD 游离在该 tag 指向的提交上，回执必须说清楚是哪一个");
            Assert.AreNotEqual("(no branch)", branch);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [TestMethod]
    public async Task Checkout_LocalBranch_ReportsBranchName()
    {
        // 对照：检出分支时不得报成游离（否则「无条件报 detached」的实现也能过上面一条）。
        var fixture = new Fixture();
        try
        {
            using var doc = await ExecuteCheckout(fixture.Root, "feature-x");
            Assert.AreEqual("feature-x", doc.RootElement.GetProperty("current_branch").GetString());
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [TestMethod]
    public async Task BranchSwitch_RemoteTrackingBranch_ReportsDetachedWithCommit()
    {
        var fixture = new Fixture();
        try
        {
            using var doc = await ExecuteSwitch(fixture.Root, "origin/master");
            var result = doc.RootElement;

            Assert.IsTrue(result.GetProperty("switched").GetBoolean());
            var name = result.GetProperty("name").GetString();
            Assert.AreEqual($"detached at {fixture.SecondCommitSha.Substring(0, 7)}", name,
                "检出远端跟踪分支会分离 HEAD；同一份回执里的 name 不能是 (no branch)，也不能假装还在某个分支上");
            Assert.AreNotEqual("(no branch)", name);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [TestMethod]
    public async Task BranchSwitch_LocalBranch_ReportsBranchName()
    {
        var fixture = new Fixture();
        try
        {
            using var doc = await ExecuteSwitch(fixture.Root, "feature-x");
            Assert.AreEqual("feature-x", doc.RootElement.GetProperty("name").GetString());
        }
        finally
        {
            fixture.Dispose();
        }
    }

    private static async Task<JsonDocument> ExecuteCheckout(string root, string target)
    {
        var result = await new GitCheckoutTool().ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "git_checkout",
            ArgumentsJson = JsonSerializer.Serialize(new { path = root, target }),
            Context = TestContext()
        });
        Assert.IsTrue(result.Success, result.Error);
        return JsonDocument.Parse(result.Output);
    }

    private static async Task<JsonDocument> ExecuteSwitch(string root, string branchName)
    {
        var result = await new GitBranchSwitchTool().ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "git_branch_switch",
            ArgumentsJson = JsonSerializer.Serialize(new { path = root, branch_name = branchName }),
            Context = TestContext()
        });
        Assert.IsTrue(result.Success, result.Error);
        return JsonDocument.Parse(result.Output);
    }

    private static ToolExecutionContext TestContext() => new()
    {
        SessionId = "test",
        WorkspaceId = "test",
        AgentInstanceId = "test"
    };

    /// <summary>
    /// 两条提交、一个 tag（指向第一条）、一个本地分支 feature-x（指向第二条）、
    /// 以及一条远端跟踪引用 refs/remotes/origin/master（指向第二条）。
    /// 远端跟踪引用用 Refs.Add 直接造，测试不碰网络。
    /// </summary>
    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "pudding-git-co-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Repository.Init(Root);

            using var repo = new Repository(Root);
            var author = new Signature("test", "test@example.invalid", DateTimeOffset.UtcNow);

            File.WriteAllText(Path.Combine(Root, "a.txt"), "a1");
            Commands.Stage(repo, "a.txt");
            var first = repo.Commit("c1", author, author);
            FirstCommitSha = first.Sha;
            repo.Tags.Add("v1", first);

            File.WriteAllText(Path.Combine(Root, "a.txt"), "a2");
            Commands.Stage(repo, "a.txt");
            var second = repo.Commit("c2", author, author);
            SecondCommitSha = second.Sha;

            repo.Branches.Add("feature-x", second);
            repo.Network.Remotes.Add("origin", "https://example.invalid/pudding.git");
            repo.Refs.Add("refs/remotes/origin/master", second.Sha);
        }

        public string Root { get; }

        public string FirstCommitSha { get; }

        public string SecondCommitSha { get; }

        public void Dispose()
        {
            // git 的松散对象在 Windows 上是只读的，清理前要还原属性，否则会把断言异常遮住。
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Root, recursive: true);
        }
    }
}
