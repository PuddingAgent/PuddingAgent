using System.Text.Json;
using LibGit2Sharp;
using PuddingCode.Tools;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// git_log 的行为契约回归（2026-11-26）。
/// 修复前这段代码里并存三处「回执说的和实际做的不是一回事」：
/// <list type="number">
/// <item>FilePath 声明了参数、写进了工具描述，却从未被读取 ⇒ 传了过滤条件仍拿到整支分支历史；</item>
/// <item>分支名不存在时 Branches[...] 返回 null，旧代码静默回落到当前分支，回执里 branch 仍写着 HEAD；</item>
/// <item>数量被截断（或 max_count 被夹取）时没有任何信号 ⇒ 无法区分「到上限了」与「历史就这么长」。</item>
/// </list>
/// 每条用例都配了一条「正常路径不产生噪声」的对照，避免用「恒为真」的实现蒙过去。
/// </summary>
[TestClass]
public sealed class GitLogToolBehaviorTests
{
    [TestMethod]
    public async Task FilePath_FiltersHistory_And_EchoesTheAppliedFilter()
    {
        // 必须用一个「命中数少于仓库总提交数」的文件：仓库共 3 条提交，b.txt 只命中 1 条。
        // 若用 a.txt（恰好 3 条 = 总数），“过滤没生效”与“过滤生效”的条数一模一样，断言抓不到。
        var root = CreateFixture();
        try
        {
            using var doc = await Log(root, new { path = root, file_path = "b.txt", max_count = 20 });
            var json = doc.RootElement;
            Assert.AreEqual("b.txt", json.GetProperty("file_path").GetString());
            Assert.AreEqual(1, json.GetProperty("count").GetInt32(), "b.txt 只被 1 次提交触及，仓库共有 3 条提交");
            Assert.IsFalse(json.GetProperty("truncated").GetBoolean());
            Assert.IsNull(json.GetProperty("notice").GetString());
        }
        finally
        {
            DeleteFixture(root);
        }
    }

    [TestMethod]
    public async Task FilePath_AcceptsAbsolutePath_ByNormalizingToRepoRelative()
    {
        var root = CreateFixture();
        try
        {
            using var doc = await Log(root, new { path = root, file_path = Path.Combine(root, "a.txt") });
            var json = doc.RootElement;
            Assert.AreEqual("a.txt", json.GetProperty("file_path").GetString(), "绝对路径应换算成仓库相对形式");
            Assert.AreEqual(3, json.GetProperty("count").GetInt32(), "a.txt 被 3 次提交触及");
        }
        finally
        {
            DeleteFixture(root);
        }
    }

    [TestMethod]
    public async Task MissingBranch_FailsInsteadOfSilentlyFallingBackToHead()
    {
        var root = CreateFixture();
        try
        {
            var result = await Execute(root, new { path = root, branch = "no-such-branch-1126" });
            Assert.IsFalse(result.Success, "指定了不存在的分支就必须失败，不能回落到当前分支");
            StringAssert.Contains(result.Error, "no-such-branch-1126");
            StringAssert.Contains(result.Error, "git_branch_list");
        }
        finally
        {
            DeleteFixture(root);
        }
    }

    [TestMethod]
    public async Task Truncation_IsReported_WhenMoreCommitsExist()
    {
        var root = CreateFixture();
        try
        {
            using var doc = await Log(root, new { path = root, max_count = 2 });
            var json = doc.RootElement;
            Assert.AreEqual(2, json.GetProperty("count").GetInt32());
            Assert.IsTrue(json.GetProperty("truncated").GetBoolean(), "仓库有 3 条提交，只要 2 条时必然截断");
            StringAssert.Contains(json.GetProperty("notice").GetString(), "older history was not returned");
        }
        finally
        {
            DeleteFixture(root);
        }
    }

    [TestMethod]
    public async Task NoTruncation_WhenRequestExceedsHistory()
    {
        // 对照：同样的 count 语义，超出历史长度时不得谎报截断。
        var root = CreateFixture();
        try
        {
            using var doc = await Log(root, new { path = root, max_count = 10 });
            var json = doc.RootElement;
            Assert.AreEqual(3, json.GetProperty("count").GetInt32());
            Assert.IsFalse(json.GetProperty("truncated").GetBoolean());
            Assert.IsNull(json.GetProperty("notice").GetString());
        }
        finally
        {
            DeleteFixture(root);
        }
    }

    [TestMethod]
    public async Task ClampedMaxCount_IsReported_EvenWhenHistoryIsShort()
    {
        // max_count 越界时，即便历史只有 3 条也必须告知「你要的和生效的不是一个数」。
        var root = CreateFixture();
        try
        {
            using var doc = await Log(root, new { path = root, max_count = 5000 });
            var json = doc.RootElement;
            Assert.AreEqual(3, json.GetProperty("count").GetInt32());
            Assert.IsFalse(json.GetProperty("truncated").GetBoolean());
            StringAssert.Contains(json.GetProperty("notice").GetString(), "clamped to the supported range 1..100");
        }
        finally
        {
            DeleteFixture(root);
        }
    }

    [TestMethod]
    public async Task ZeroMaxCount_ClampsToOne_AndReportsBothClampAndTruncation()
    {
        var root = CreateFixture();
        try
        {
            using var doc = await Log(root, new { path = root, max_count = 0 });
            var json = doc.RootElement;
            Assert.AreEqual(1, json.GetProperty("count").GetInt32());
            Assert.IsTrue(json.GetProperty("truncated").GetBoolean());
            var notice = json.GetProperty("notice").GetString();
            StringAssert.Contains(notice, "max_count=0 was clamped");
            StringAssert.Contains(notice, "older history was not returned");
        }
        finally
        {
            DeleteFixture(root);
        }
    }

    private static async Task<JsonDocument> Log(string root, object arguments)
    {
        var result = await Execute(root, arguments);
        Assert.IsTrue(result.Success, result.Error);
        return JsonDocument.Parse(result.Output);
    }

    private static Task<ToolExecutionResult> Execute(string root, object arguments) =>
        new GitLogTool().ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "git_log",
            ArgumentsJson = JsonSerializer.Serialize(arguments),
            Context = new ToolExecutionContext
            {
                SessionId = "test",
                WorkspaceId = "test",
                AgentInstanceId = "test"
            }
        });

    /// <summary>
    /// 三条提交：c1 建 a.txt+b.txt，c2/c3 各改一次 a.txt。
    /// 于是 a.txt 命中 3 条、b.txt 命中 1 条 —— 文件维度与分支维度的条数不同，
    /// 才能区分「过滤生效」和「碰巧条数相同」。
    /// </summary>
    private static string CreateFixture()
    {
        var root = Path.Combine(Path.GetTempPath(), "pudding-git-log", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Repository.Init(root);
        using var repo = new Repository(root);
        var author = new Signature("test", "test@example.invalid", DateTimeOffset.UtcNow);

        File.WriteAllText(Path.Combine(root, "a.txt"), "a1");
        File.WriteAllText(Path.Combine(root, "b.txt"), "b1");
        Commands.Stage(repo, new[] { "a.txt", "b.txt" });
        repo.Commit("c1", author, author);

        File.WriteAllText(Path.Combine(root, "a.txt"), "a2");
        Commands.Stage(repo, new[] { "a.txt" });
        repo.Commit("c2", author, author);

        File.WriteAllText(Path.Combine(root, "a.txt"), "a3");
        Commands.Stage(repo, new[] { "a.txt" });
        repo.Commit("c3", author, author);

        return root;
    }

    private static void DeleteFixture(string root)
    {
        // git 的松散对象在 Windows 上是只读的，清理不能把断言异常掩盖掉。
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, recursive: true);
    }
}
