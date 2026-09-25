using PuddingFullTextIndex.Cli;
using PuddingFullTextIndex.Contracts;

namespace PuddingFullTextIndex.Cli.Tests;

/// <summary>
/// <c>build</c> / <c>cancel</c>：
/// A4（临时索引根 + 夹具语料 + 真 Lucene ⇒ 退出 0、<c>IndexedFileCount ≥ 1</c>，随后 <c>status</c> 看到索引）、
/// A6（已在 <see cref="SupplyCliParsingTests"/> 覆盖）、A3（Rejected→2 / Busy→3 / Failed→4）、
/// 跨进程 cancel 如实失败，以及 <see cref="ScopeMirrorGolden"/>（A19 冻结的旧镜像）与真实组件的交叉断言。
/// </summary>
[TestClass]
public sealed class SupplyCliBuildTests
{
    /// <summary>A4：<c>build --wait</c> 用真 Lucene 在**临时**索引根下构建成功；随后 status 如实看到索引。</summary>
    [TestMethod]
    public void Build_Wait_With_Real_Lucene_Succeeds_And_Status_Sees_The_Index()
    {
        using var fixture = new CliFixture();
        fixture.Write("alpha.cs", "class Alpha { public int Value => 42; }");
        fixture.Write("notes.md", "# 全文索引\n夹具语料。");

        var host = new CliTestHost();

        var build = fixture.Run(
            "build",
            "--scope", fixture.Corpus,
            "--index-root", fixture.IndexRoot,
            "--wait");

        Assert.AreEqual(0, build.ExitCode, $"{build.StdErr}\n{build.StdOut}");
        Assert.AreEqual("Started", build.Value("outcome"));
        Assert.AreEqual("Succeeded", build.Value("state"));
        Assert.AreEqual("Completed", build.Value("phase"));
        Assert.AreEqual("builder", build.Value("metrics-source"), "度量必须来自本进程真实 builder");

        var indexedFileCount = build.IntValue("IndexedFileCount");
        Assert.IsGreaterThanOrEqualTo(1, indexedFileCount, $"IndexedFileCount 必须 >= 1，实际 {indexedFileCount}");
        Assert.AreEqual(2, indexedFileCount, "夹具正好两个可索引文件");
        Assert.IsGreaterThan(0L, build.LongValue("TotalBytes"));
        Assert.IsGreaterThanOrEqualTo(0L, build.LongValue("ElapsedMs"));
        Assert.AreEqual(0, build.IntValue("exit-code"));

        // status 必须如实看到索引（HasIndex 走引擎公开判定）
        var status = fixture.Run(
            "status",
            "--scope", fixture.Corpus,
            "--index-root", fixture.IndexRoot);

        Assert.AreEqual(0, status.ExitCode, status.StdErr);
        Assert.AreEqual("true", status.Value("scope[0].hasIndex"));
        Assert.AreEqual("true", status.Value("scope[0].indexDirectoryExists"));
        Assert.IsGreaterThan(0, status.IntValue("scope[0].indexEntryCount"), "索引目录必须有条目");
        Assert.IsGreaterThan(0L, status.LongValue("scope[0].indexBytes"), "索引目录必须有字节");
        Assert.AreEqual("none", status.Value("scope[0].lease"), "构建结束后租约必须已释放");
    }

    /// <summary>
    /// 交叉断言 ①：<c>--json</c> 的 <c>scopes[0].scopeKey</c> 由协调器（A1 内部规范化）产生，
    /// 必须与 CLI 镜像的 <c>ToScopeKey</c> 一致 —— 镜像漂移就红（A19：oracle 换成冻结副本 <see cref="ScopeMirrorGolden"/>）。
    /// </summary>
    [TestMethod]
    public void Build_Json_Scope_Key_Matches_The_Component_Normalizer()
    {
        using var fixture = new CliFixture();
        fixture.Write("alpha.cs", "class Alpha { }");

        var run = fixture.Run(
            "build", "--scope", fixture.Corpus, "--index-root", fixture.IndexRoot, "--wait", "--json");

        Assert.AreEqual(0, run.ExitCode, $"{run.StdErr}\n{run.StdOut}");
        using var json = run.Json();
        var root = json.RootElement;

        Assert.AreEqual("build", root.GetProperty("command").GetString());
        Assert.AreEqual("Started", root.GetProperty("outcome").GetString());
        Assert.AreEqual("Succeeded", root.GetProperty("state").GetString());
        Assert.AreEqual("builder", root.GetProperty("metricsSource").GetString());
        Assert.IsGreaterThanOrEqualTo(1, root.GetProperty("indexedFileCount").GetInt32());
        Assert.AreEqual(0, root.GetProperty("exitCode").GetInt32());

        var componentScopeKey = root.GetProperty("scopes")[0].GetProperty("scopeKey").GetString();
        Assert.AreEqual(
            ScopeMirrorGolden.ToScopeKey(ScopeMirrorGolden.NormalizeRoot(fixture.Corpus)),
            componentScopeKey,
            "CLI 镜像的 scopeKey 必须与协调器内部口径逐字一致");

        // 交叉断言 ②：镜像解析出的索引目录必须**就是**磁盘上引擎实际建出的那个目录
        var mirroredIndexDirectory = ScopeMirrorGolden.ResolveIndexDirectory(fixture.IndexRoot, fixture.Corpus);
        Assert.IsTrue(Directory.Exists(mirroredIndexDirectory), $"镜像解析的索引目录必须真实存在：{mirroredIndexDirectory}");

        var engineDirectories = Directory
            .GetDirectories(fixture.IndexRoot)
            .Where(d => !Path.GetFileName(d).StartsWith('.'))
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.HasCount(
            1, engineDirectories, $"临时索引根下应只有一个 hash 目录：{string.Join("; ", engineDirectories)}");
        Assert.AreEqual(
            Path.GetFullPath(engineDirectories[0]).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(mirroredIndexDirectory).TrimEnd(Path.DirectorySeparatorChar),
            "镜像口径与引擎实际使用的索引目录不一致");
    }

    /// <summary>A3：<c>Rejected</c> ⇒ 退出码 2。</summary>
    [TestMethod]
    public void Build_On_A_Missing_Scope_Is_Rejected_With_Exit_Code_2()
    {
        using var fixture = new CliFixture();

        var run = fixture.Run("build", "--scope", fixture.MissingDirectory, "--index-root", fixture.IndexRoot);

        Assert.AreEqual(2, run.ExitCode);
        Assert.AreEqual("Rejected", run.Value("outcome"));
        StringAssert.Contains(run.Value("scope[0].reason"), "不存在");
        Assert.AreEqual("none", run.Value("jobId"));
        Assert.AreEqual(2, run.IntValue("exit-code"));
    }

    /// <summary>A3：<c>Busy</c>（租约被别的 owner 持有）⇒ 退出码 3，且**不构建、不写索引**。</summary>
    [TestMethod]
    public void Build_When_The_Lease_Is_Held_Elsewhere_Is_Busy_With_Exit_Code_3()
    {
        using var fixture = new CliFixture();
        fixture.Write("alpha.cs", "class Alpha { }");

        var lease = new AlwaysBusyLease();
        var host = new CliTestHost
        {
            LeaseOverride = lease,
            BuildBehaviour = (_, _) => throw new InvalidOperationException("Busy 时不得构建。"),
        };

        var run = fixture.RunWith(host, "build", "--scope", fixture.Corpus, "--index-root", fixture.IndexRoot);

        Assert.AreEqual(3, run.ExitCode);
        Assert.AreEqual("Busy", run.Value("outcome"));
        Assert.AreEqual("Busy", run.Value("scope[0].outcome"));
        StringAssert.Contains(run.Value("scope[0].reason"), "other-machine#4242");
        Assert.AreEqual("none", run.Value("jobId"), "Busy 不产生 job");

        Assert.IsNotNull(host.SuppliedBuilder);
        Assert.AreEqual(0, host.SuppliedBuilder.BuildCallCount, "Busy 不得触发构建");
        Assert.AreEqual(1, lease.AcquireCalls, "Busy 路径只尝试取租约一次，不重试、不构建");
        Assert.IsEmpty(CliFixture.CaptureEntries(fixture.IndexRoot), "Busy 不得写索引根");
        Assert.AreEqual(3, run.IntValue("exit-code"));
    }

    /// <summary>不带 <c>--wait</c> 时只报「已受理」（退出码 0），这不等于构建成功。</summary>
    [TestMethod]
    public void Build_Without_Wait_Only_Reports_Acceptance()
    {
        using var fixture = new CliFixture();
        fixture.Write("alpha.cs", "class Alpha { }");

        var host = new CliTestHost
        {
            BuildBehaviour = (_, _) => Task.FromResult(StubIndexBuilder.Failed("替身：构建失败。")),
        };

        var accepted = fixture.RunWith(host, "build", "--scope", fixture.Corpus, "--index-root", fixture.IndexRoot);

        Assert.AreEqual(0, accepted.ExitCode, accepted.StdErr);
        Assert.AreEqual("Started", accepted.Value("outcome"));
        Assert.IsFalse(string.IsNullOrWhiteSpace(accepted.Value("jobId")));
        Assert.AreEqual("false", accepted.Value("wait"));
    }

    /// <summary>A3：<c>Failed</c> ⇒ 退出码 4（<b>M2 变异点</b>：吞掉失败 ⇒ 本用例必红）。</summary>
    [TestMethod]
    public void Build_With_A_Failing_Builder_Exits_4_When_Waiting()
    {
        using var fixture = new CliFixture();
        fixture.Write("alpha.cs", "class Alpha { }");

        var host = new CliTestHost
        {
            BuildBehaviour = (_, _) => Task.FromResult(StubIndexBuilder.Failed("替身：构建失败。")),
        };

        var waited = fixture.RunWith(
            host, "build", "--scope", fixture.Corpus, "--index-root", fixture.IndexRoot, "--wait");

        Assert.AreEqual(4, waited.ExitCode, $"构建失败不得返回 0：\n{waited.StdOut}");
        Assert.AreEqual("Failed", waited.Value("state"));
        StringAssert.Contains(waited.Value("job-message"), "替身：构建失败。");
        Assert.AreEqual(4, waited.IntValue("exit-code"));
    }

    /// <summary><c>--wait</c> 期间本进程看不到 job ⇒ 如实报告 <c>not_in_this_process</c> + 退出码 3。</summary>
    [TestMethod]
    public void Build_Wait_On_A_Job_This_Process_Cannot_See_Reports_Not_In_This_Process()
    {
        using var fixture = new CliFixture();
        fixture.Write("alpha.cs", "class Alpha { }");

        var fake = new FakeCoordinator
        {
            // 受理了，但台账里没有这条 job ⇒ 本进程无法观察
            NextBuildOutcome = new SupplyRequestOutcome(SupplyOutcome.Started, "invisible-job", "替身：已受理。", null, []),
        };

        var host = new CliTestHost { CoordinatorOverride = fake };

        var run = fixture.RunWith(
            host, "build", "--scope", fixture.Corpus, "--index-root", fixture.IndexRoot, "--wait");

        Assert.AreEqual(3, run.ExitCode);
        Assert.AreEqual("not_in_this_process", run.Value("wait-note"));
        Assert.AreEqual("none", run.Value("state"), "看不到 job 时不得编造状态");

        foreach (var wording in new[] { "无 job", "无此 job", "no such job" })
            Assert.IsFalse(run.StdOut.Contains(wording, StringComparison.Ordinal), $"不得出现会引起误解的措辞：{wording}");
    }

    /// <summary><c>--wait</c> 超时（job 仍在 Running）⇒ 退出码 3 + <c>wait-timeout</c>，绝不谎报成功。</summary>
    [TestMethod]
    public void Build_Wait_Times_Out_While_The_Job_Is_Still_Running()
    {
        using var fixture = new CliFixture();
        fixture.Write("alpha.cs", "class Alpha { }");

        var fake = new FakeCoordinator
        {
            NextBuildOutcome = new SupplyRequestOutcome(SupplyOutcome.Started, "running-job", "替身：已受理。", null, []),
        };
        fake.AddJob(FakeCoordinator.Job(
            "running-job", SupplyJobState.Running, SupplyJobPhases.Building, fixture.Corpus,
            DateTimeOffset.UtcNow, finishedAt: null, discoveredFileCount: 1, discoveredBytes: 15, message: "正在构建。"));

        var host = new CliTestHost { CoordinatorOverride = fake, BuildTimeout = TimeSpan.Zero };

        var run = fixture.RunWith(
            host, "build", "--scope", fixture.Corpus, "--index-root", fixture.IndexRoot, "--wait");

        Assert.AreEqual(3, run.ExitCode, "超时不得返回 0");
        Assert.AreEqual("wait-timeout", run.Value("wait-note"));
        Assert.AreEqual("Running", run.Value("state"));
        Assert.AreEqual(3, run.IntValue("exit-code"));
        Assert.AreEqual(1, fake.BuildCalls, "只提交一次供给请求（不重试）");
    }

    /// <summary>
    /// 本进程没跑构建（合并到已有成功 job）时，退化到 job 的发现计数并**如实标注**度量来源。
    /// </summary>
    [TestMethod]
    public void Build_Wait_On_An_Already_Succeeded_Job_Labels_Job_Discovery_Metrics()
    {
        using var fixture = new CliFixture();
        var started = DateTimeOffset.UtcNow.AddSeconds(-2);
        var finished = started.AddSeconds(1);

        var fake = new FakeCoordinator
        {
            NextBuildOutcome = new SupplyRequestOutcome(
                SupplyOutcome.Merged, "fake-job", "替身：合并到已有成功 job。", null, []),
        };
        fake.AddJob(FakeCoordinator.Job(
            "fake-job", SupplyJobState.Succeeded, SupplyJobPhases.Completed, fixture.Corpus,
            started, finished, discoveredFileCount: 7, discoveredBytes: 4096, message: "构建完成：7 文件 / 4096 字节 / 12 ms。"));

        var host = new CliTestHost { CoordinatorOverride = fake };

        var run = fixture.RunWith(
            host, "build", "--scope", fixture.Corpus, "--index-root", fixture.IndexRoot, "--wait");

        Assert.AreEqual(0, run.ExitCode, run.StdErr);
        Assert.AreEqual("Merged", run.Value("outcome"));
        Assert.AreEqual("Succeeded", run.Value("state"));
        Assert.AreEqual("job-discovery", run.Value("metrics-source"), "不得把发现计数伪装成 builder 实测");
        Assert.AreEqual(7, run.IntValue("IndexedFileCount"));
        Assert.AreEqual(4096L, run.LongValue("TotalBytes"));
        Assert.AreEqual(1000L, run.LongValue("ElapsedMs"), "无 builder 结果时用 job 时间线（finishedAt - startedAt）");
    }

    /// <summary>跨进程 <c>cancel</c>：本进程台账里没有该 job ⇒ 逐字 <c>not_in_this_process</c> + 非零退出。</summary>
    [TestMethod]
    public void Cancel_For_A_Foreign_Job_Reports_Not_In_This_Process_And_Exits_Non_Zero()
    {
        using var fixture = new CliFixture();

        var run = fixture.Run("cancel", "--job", "job-in-another-process");

        Assert.AreEqual(3, run.ExitCode, "跨进程取消不得静默返回成功");
        Assert.AreEqual("not_in_this_process", run.Value("lookup"));
        StringAssert.Contains(run.StdOut, "not_in_this_process");
        Assert.AreEqual("none", run.Value("state"));
        Assert.AreEqual(3, run.IntValue("exit-code"));

        foreach (var wording in new[] { "无 job", "无此 job", "无任务", "no such job" })
            Assert.IsFalse(run.StdOut.Contains(wording, StringComparison.Ordinal), $"不得出现会引起误解的措辞：{wording}");
    }

    /// <summary>本进程台账命中且取消成功 ⇒ 退出码 0（本进程真的执行了取消才允许 0）。</summary>
    [TestMethod]
    public void Cancel_For_A_Job_In_This_Process_Succeeds()
    {
        using var fixture = new CliFixture();

        var fake = new FakeCoordinator { CancelSucceeds = true };
        fake.AddJob(FakeCoordinator.Job(
            "job-1", SupplyJobState.Running, SupplyJobPhases.Building, fixture.Corpus,
            DateTimeOffset.UtcNow, finishedAt: null, discoveredFileCount: 1, discoveredBytes: 15, message: "正在构建。"));

        var host = new CliTestHost { CoordinatorOverride = fake };

        var run = fixture.RunWith(host, "cancel", "--job", "job-1");

        Assert.AreEqual(0, run.ExitCode, run.StdErr);
        Assert.AreEqual("in_this_process", run.Value("lookup"));
        Assert.AreEqual(1, fake.CancelCalls, "必须真的调用协调器的 CancelAsync");
    }

    /// <summary>协调器拒绝取消（已是终态）⇒ 不得报成功。</summary>
    [TestMethod]
    public void Cancel_When_The_Coordinator_Refuses_Exits_Non_Zero()
    {
        using var fixture = new CliFixture();

        var fake = new FakeCoordinator { CancelSucceeds = false };
        fake.AddJob(FakeCoordinator.Job(
            "job-1", SupplyJobState.Succeeded, SupplyJobPhases.Completed, fixture.Corpus,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1, 15, "构建完成。"));

        var host = new CliTestHost { CoordinatorOverride = fake };

        var run = fixture.RunWith(host, "cancel", "--job", "job-1");

        Assert.AreEqual(3, run.ExitCode, "取消未发生 ⇒ 不得返回 0");
        Assert.AreEqual("in_this_process", run.Value("lookup"));
        StringAssert.Contains(run.Value("detail"), "未发生取消");
    }

    /// <summary>cancel 的 JSON 形状。</summary>
    [TestMethod]
    public void Cancel_Json_Has_The_Expected_Shape()
    {
        using var fixture = new CliFixture();

        var run = fixture.Run("cancel", "--job", "job-in-another-process", "--json");

        Assert.AreEqual(3, run.ExitCode);
        using var json = run.Json();
        var root = json.RootElement;

        Assert.AreEqual("cancel", root.GetProperty("command").GetString());
        Assert.AreEqual("job-in-another-process", root.GetProperty("jobId").GetString());
        Assert.AreEqual("not_in_this_process", root.GetProperty("lookup").GetString());
        Assert.AreEqual(3, root.GetProperty("exitCode").GetInt32());
    }
}
