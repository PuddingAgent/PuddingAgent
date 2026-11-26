using LibGit2Sharp;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// HEAD 身份判据（2026-11-26）。
///
/// 修的是「回执说的和实际做的不是一回事」的第五处：LibGit2Sharp 在游离 HEAD 下把
/// <c>Head.FriendlyName</c> 报成字面量 <c>"(no branch)"</c>。git_log / git_status 会把它当分支名回给调用方，
/// git_push / git_pull 则会把它拼进 <c>refs/heads/(no branch)</c> 这种垃圾 refspec。
/// </summary>
[TestClass]
public class GitHeadIdentityTests
{
    [TestMethod]
    public void Describe_OnCheckedOutBranch_ReturnsThatBranchName()
    {
        using var fixture = new GitFixture();
        fixture.CheckoutNewBranch("fixture-branch");

        Assert.AreEqual("fixture-branch", GitHeadIdentity.Describe(fixture.Repo));
    }

    [TestMethod]
    public void Describe_DetachedHead_ReportsCommit_NotLibGit2Placeholder()
    {
        using var fixture = new GitFixture();
        fixture.DetachAtFirstCommit();

        var described = GitHeadIdentity.Describe(fixture.Repo);

        Assert.AreEqual($"detached at {fixture.FirstCommitSha.Substring(0, 7)}", described);
        // 反向断言：(no branch) 是旧实现的输出，它让调用方以为「这个仓库没有分支」。
        Assert.AreNotEqual("(no branch)", described);
    }

    [TestMethod]
    public void TryResolveBranchName_OnBranchWithoutRequested_UsesCurrentBranch()
    {
        using var fixture = new GitFixture();
        fixture.CheckoutNewBranch("fixture-branch");

        Assert.IsTrue(GitHeadIdentity.TryResolveBranchName(fixture.Repo, null, out var name, out var error));
        Assert.AreEqual("fixture-branch", name);
        Assert.IsNull(error);
    }

    [TestMethod]
    public void TryResolveBranchName_ExplicitBranchWins_EvenWhenDetached()
    {
        using var fixture = new GitFixture();
        fixture.DetachAtFirstCommit();

        Assert.IsTrue(
            GitHeadIdentity.TryResolveBranchName(fixture.Repo, "hotfix-x", out var name, out var error),
            "显式指定分支本来就是合法用法，不能因为 HEAD 游离就拒绝");
        Assert.AreEqual("hotfix-x", name);
        Assert.IsNull(error);
    }

    [TestMethod]
    public void TryResolveBranchName_DetachedWithoutRequested_FailsWithActionableReason()
    {
        using var fixture = new GitFixture();
        fixture.DetachAtFirstCommit();

        Assert.IsFalse(GitHeadIdentity.TryResolveBranchName(fixture.Repo, null, out var name, out var error));
        Assert.AreEqual(string.Empty, name, "失败时不应留下半成品分支名");
        StringAssert.Contains(error, "detached at", "必须说明 HEAD 游离在哪个提交上");
        StringAssert.Contains(error, "git_branch_switch", "必须给出可执行的下一步");
    }

    /// <summary>
    /// 两个提交、一个有名字的临时仓库。git 的松散对象在 Windows 上是只读的，清理前要还原属性。
    /// </summary>
    private sealed class GitFixture : IDisposable
    {
        private readonly string _root;

        public GitFixture()
        {
            _root = Path.Combine(Path.GetTempPath(), "pudding-githead-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            Repository.Init(_root);
            Repo = new Repository(_root);

            var author = new Signature("test", "test@example.invalid", DateTimeOffset.UtcNow);

            File.WriteAllText(Path.Combine(_root, "a.txt"), "a1");
            Commands.Stage(Repo, "a.txt");
            FirstCommitSha = Repo.Commit("c1", author, author).Sha;

            File.WriteAllText(Path.Combine(_root, "a.txt"), "a2");
            Commands.Stage(Repo, "a.txt");
            Repo.Commit("c2", author, author);
        }

        public Repository Repo { get; }

        public string FirstCommitSha { get; }

        public void CheckoutNewBranch(string name)
            => Commands.Checkout(Repo, Repo.CreateBranch(name));

        public void DetachAtFirstCommit()
            => Commands.Checkout(Repo, Repo.Lookup<Commit>(FirstCommitSha));

        public void Dispose()
        {
            Repo.Dispose();
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_root, recursive: true);
        }
    }
}
