using System.Text.Json;
using PuddingFullTextIndex.Cli;
using PuddingFullTextIndex.Contracts;
using PuddingFullTextIndex.Infrastructure.Supply;

namespace PuddingFullTextIndex.Cli.Tests;

/// <summary>
/// <c>status</c>：A5（<c>--job</c> 查不到 ⇒ 逐字 <c>not_in_this_process</c>，且不得出现"无 job"这类会误导的措辞）、
/// 磁盘可观察状态、本进程命中分支、<c>--index-root</c> 省略时如实打印默认解析、JSON 形状。
/// </summary>
[TestClass]
public sealed class SupplyCliStatusTests
{
    private static readonly string[] MisleadingWording =
    [
        "无 job", "无此 job", "无任务", "no such job", "job not found", "任务不存在", "不存在该 job",
    ];

    /// <summary>A5：本进程查不到时，必须逐字说 <c>not_in_this_process</c>，绝不外推状态。</summary>
    [TestMethod]
    public void Status_Job_Lookup_Miss_Reports_Not_In_This_Process()
    {
        using var fixture = new CliFixture();

        var run = fixture.Run("status", "--job", "no-such-job", "--index-root", fixture.IndexRoot);

        Assert.AreEqual(0, run.ExitCode, "status 只做只读报告：报告本身成功即退出码 0");
        StringAssert.Contains(run.StdOut, "not_in_this_process");
        Assert.AreEqual("not_in_this_process", run.Value("job-lookup"));
        Assert.AreEqual("none", run.Value("job-lookup-state"), "查不到时不得给出任何状态（不猜）");

        foreach (var wording in MisleadingWording)
            Assert.IsFalse(run.StdOut.Contains(wording, StringComparison.Ordinal), $"不得出现会引起误解的措辞：{wording}");
    }

    [TestMethod]
    public void Status_Json_Job_Lookup_Miss_Reports_Not_In_This_Process()
    {
        using var fixture = new CliFixture();

        var run = fixture.Run("status", "--job", "no-such-job", "--index-root", fixture.IndexRoot, "--json");

        Assert.AreEqual(0, run.ExitCode);
        using var json = run.Json();
        var lookup = json.RootElement.GetProperty("jobLookup");

        Assert.AreEqual("no-such-job", lookup.GetProperty("jobId").GetString());
        Assert.AreEqual("not_in_this_process", lookup.GetProperty("lookup").GetString());
        Assert.AreEqual(JsonValueKind.Null, lookup.GetProperty("job").ValueKind, "查不到时 job 必须是 null（不猜）");
    }

    /// <summary>本进程命中分支（用协调器替身预置 job 台账）。</summary>
    [TestMethod]
    public void Status_Job_Lookup_Hit_Reports_The_Job_From_This_Process()
    {
        using var fixture = new CliFixture();
        var started = DateTimeOffset.UtcNow.AddSeconds(-2);
        var finished = DateTimeOffset.UtcNow.AddSeconds(-1);

        var fake = new FakeCoordinator();
        fake.AddJob(FakeCoordinator.Job(
            "job-1", SupplyJobState.Succeeded, SupplyJobPhases.Completed, fixture.Corpus,
            started, finished, discoveredFileCount: 7, discoveredBytes: 4096, message: "构建完成：7 文件 / 4096 字节 / 12 ms。"));

        var host = new CliTestHost { CoordinatorOverride = fake };

        var run = fixture.RunWith(host, "status", "--job", "job-1", "--index-root", fixture.IndexRoot);

        Assert.AreEqual(0, run.ExitCode, run.StdErr);
        Assert.AreEqual("in_this_process", run.Value("job-lookup"));
        Assert.AreEqual("Succeeded", run.Value("job-lookup-state"));
        Assert.AreEqual(1, run.IntValue("job-count"));
        Assert.AreEqual("job-1", run.Value("job[0].jobId"));
        Assert.AreEqual(7, run.IntValue("job[0].discoveredFileCount"));
        Assert.AreEqual(4096L, run.LongValue("job[0].discoveredBytes"));
    }

    /// <summary>磁盘可观察状态：还没建索引 ⇒ <c>hasIndex=false</c>、索引目录不存在、无租约。</summary>
    [TestMethod]
    public void Status_Reports_Disk_Observable_State_Without_An_Index()
    {
        using var fixture = new CliFixture();
        fixture.Write("a.cs", "class A { }");

        var run = fixture.Run("status", "--scope", fixture.Corpus, "--index-root", fixture.IndexRoot);

        Assert.AreEqual(0, run.ExitCode, run.StdErr);
        Assert.AreEqual(1, run.IntValue("scope-count"));
        Assert.AreEqual("true", run.Value("scope[0].exists"));
        Assert.AreEqual("false", run.Value("scope[0].hasIndex"));
        Assert.AreEqual("false", run.Value("scope[0].indexDirectoryExists"));
        Assert.AreEqual(0, run.IntValue("scope[0].indexEntryCount"));
        Assert.AreEqual(0L, run.LongValue("scope[0].indexBytes"));
        Assert.AreEqual("none", run.Value("scope[0].lease"));
        Assert.AreEqual("false", run.Value("index-root-exists"));
        Assert.AreEqual(0, run.IntValue("index-root-top-level-entries"));

        // 索引目录必须是「A19 冻结金标准解析出的那个」（与磁盘上引擎会用的目录同口径）
        var expectedIndexDirectory = ScopeMirrorGolden.ResolveIndexDirectory(fixture.IndexRoot, fixture.Corpus);
        Assert.AreEqual(expectedIndexDirectory, run.Value("scope[0].indexDirectory"));

        Assert.IsFalse(Directory.Exists(fixture.IndexRoot), "status 只读：不得创建索引根");
    }

    /// <summary>租约持有者必须可读（读 <c>.supply-leases</c>）：先用真实租约占住，再让 status 观察。</summary>
    [TestMethod]
    public void Status_Reports_The_Lease_Holder_When_One_Exists()
    {
        using var fixture = new CliFixture();
        fixture.Write("a.cs", "class A { }");

        var options = new FullTextIndexOptions { IndexRootDirectory = fixture.IndexRoot };
        var lease = new FileSupplyLease(options);
        var scopeKey = ScopeMirrorGolden.ToScopeKey(ScopeMirrorGolden.NormalizeRoot(fixture.Corpus));

        var acquired = lease
            .TryAcquireAsync(scopeKey, new SupplyLeaseOwner("holder-owner", 4242, "unit-test-machine"), "job-held")
            .GetAwaiter()
            .GetResult();
        Assert.IsTrue(acquired.Acquired, acquired.Message);

        try
        {
            var run = fixture.Run("status", "--scope", fixture.Corpus, "--index-root", fixture.IndexRoot);

            Assert.AreEqual(0, run.ExitCode, run.StdErr);
            Assert.AreEqual("holder", run.Value("scope[0].lease"));
            Assert.AreEqual("holder-owner", run.Value("scope[0].leaseOwner"));
            Assert.AreEqual("job-held", run.Value("scope[0].leaseJobId"));
        }
        finally
        {
            lease.ReleaseAsync(scopeKey, "holder-owner").GetAwaiter().GetResult();
        }
    }

    /// <summary>省略 <c>--index-root</c> 时必须如实打印"默认解析"的结果（R3）。</summary>
    [TestMethod]
    public void Status_Prints_The_Default_Index_Root_When_Omitted()
    {
        using var fixture = new CliFixture();
        fixture.Write("a.cs", "class A { }");

        var run = fixture.Run("status", "--scope", fixture.Corpus);

        Assert.AreEqual(0, run.ExitCode, run.StdErr);
        Assert.AreEqual("default", run.Value("index-root-source"));

        var defaultRoot = new FullTextIndexOptions().IndexRootDirectory;
        Assert.AreEqual(defaultRoot, run.Value("index-root"), "必须打印引擎默认解析出的索引根（而不是夹具的临时根）");

        // 夹具的临时根只用于 scope，绝不能被当成索引根
        Assert.AreNotEqual(fixture.IndexRoot, run.Value("index-root"));
        Assert.IsFalse(Directory.Exists(fixture.IndexRoot), "status 只读：不得创建夹具的索引根");
    }

    [TestMethod]
    public void Status_Json_Has_The_Expected_Shape()
    {
        using var fixture = new CliFixture();
        fixture.Write("a.cs", "class A { }");

        var run = fixture.Run("status", "--scope", fixture.Corpus, "--index-root", fixture.IndexRoot, "--json");

        Assert.AreEqual(0, run.ExitCode, run.StdErr);
        using var json = run.Json();
        var root = json.RootElement;

        Assert.AreEqual("status", root.GetProperty("command").GetString());
        Assert.AreEqual(0, root.GetProperty("exitCode").GetInt32());
        Assert.AreEqual(0, root.GetProperty("jobs").GetArrayLength());
        Assert.AreEqual(JsonValueKind.Null, root.GetProperty("jobLookup").ValueKind, "未给 --job 时不得有 lookup");

        var scope = root.GetProperty("scopes")[0];
        Assert.AreEqual(fixture.Corpus, scope.GetProperty("scopePath").GetString());
        Assert.IsFalse(scope.GetProperty("hasIndex").GetBoolean());
        Assert.IsTrue(scope.GetProperty("scopeExists").GetBoolean());
        Assert.AreEqual("none", scope.GetProperty("leaseLookup").GetString());
        Assert.AreEqual(JsonValueKind.Null, scope.GetProperty("leaseHolder").ValueKind);
    }

    [TestMethod]
    public void Status_Without_Job_Or_Scope_Still_Reports_The_Environment()
    {
        using var fixture = new CliFixture();

        var run = fixture.Run("status", "--index-root", fixture.IndexRoot);

        Assert.AreEqual(0, run.ExitCode, run.StdErr);
        Assert.AreEqual(0, run.IntValue("scope-count"));
        Assert.AreEqual(0, run.IntValue("job-count"));
        Assert.IsFalse(string.IsNullOrWhiteSpace(run.Value("owner")), "status 必须打印 owner 以便诊断" );
    }
}
