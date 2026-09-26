using PuddingFullTextIndex;
using PuddingFullTextIndex.Contracts;
using PuddingFullTextIndex.Infrastructure;
using PuddingFullTextIndex.Infrastructure.Maintenance;
using PuddingFullTextIndex.Infrastructure.Search;
using PuddingFullTextIndex.Infrastructure.Supply;
using Directory = System.IO.Directory;

namespace PuddingFullTextIndexTests;

/// <summary>
/// 缺陷修复回归钉：**同一语料根上「维护直写」与「供给整目录替换」必须互斥**。
/// <para>
/// 权威规格：任务书 <c>temp/fix-cross-path-exclusion-task.md</c>（§1 两处成因、§2 F1~F6、§3 M-F1/M-F2、§4 门禁）。
/// </para>
/// <para>
/// 修的两处成因：
/// <list type="number">
/// <item><description><b>① 盘根键不一致 + 前缀判定在盘根键下失效</b>：两侧都用裸 <c>TrimEnd</c> / 保盘根 <c>TrimEnd</c>
/// 的不同口径 ⇒ 盘根得到 <c>c:</c> 与 <c>c:\</c> 两个键 ⇒ 不同租约文件；且前缀判定无条件的
/// <c>ancestor + "\\"</c> 让盘根键拼出 <c>c:\\</c> ⇒ 嵌套 / 越界判定恒 false。
/// 修法：<c>SupplyScopeNormalizer</c> 收敛为**单一真源**（保盘根裁剪 + 唯一祖先判定 + 唯一 scope 内判定）。</description></item>
/// <item><description><b>② 同进程默认身份不互斥</b>：租约以 <c>OwnerId</c> 判「自己人」而重入，而两条路径的默认 owner 相同。
/// 修法：owner 身份加**角色**维度（<c>机器名#进程号#角色</c>），<c>ForCurrentProcess</c> 必须传角色（删除无参重载）。</description></item>
/// </list>
/// </para>
/// <para>
/// 红线：语料与索引根一律落在 <c>%TEMP%</c>（<see cref="Initialize"/> 有硬断言、绝不以 <c>D:\data</c> 开头）；
/// 盘根用例**只算键与路径、不写租约文件**（<c>C:\</c> 可能无写权限）；本文件零 git 操作。
/// </para>
/// </summary>
[TestClass]
public sealed class CrossPathExclusionFixTests
{
    private const long DefaultMaxIndexBytes = 1_073_741_824L;
    private const int DefaultMaxPaths = 512;

    public TestContext TestContext { get; set; } = null!;

    private string _root = null!;
    private string _indexRoot = null!;
    private string _corpus = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "pudding-fts-fix-" + Guid.NewGuid().ToString("N"));

        // 硬断言（红线 R3）：语料与索引只允许落在 %TEMP% 下，绝不碰生产索引根 D:\data\fulltext-index
        Assert.IsTrue(
            _root.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase),
            $"测试根必须在 %TEMP% 下，实际 {_root}");
        Assert.IsFalse(
            _root.StartsWith(@"D:\data", StringComparison.OrdinalIgnoreCase),
            $"测试根绝不允许落在生产数据根下，实际 {_root}");

        _indexRoot = Path.Combine(_root, "index");
        _corpus = Path.Combine(_root, "corpus");
        Directory.CreateDirectory(_corpus);

        Assert.IsFalse(
            _indexRoot.StartsWith(@"D:\data", StringComparison.OrdinalIgnoreCase),
            $"临时索引根绝不允许落在生产数据根下，实际 {_indexRoot}");
    }

    /// <summary>
    /// 失败路径（断言失败 / 变异取红）也必须把 <c>%TEMP%</c> 清干净；清不掉就响亮报错（转红），绝不静默。
    /// </summary>
    [TestCleanup]
    public void Cleanup()
    {
        var remaining = TryDeleteRoot();

        for (var attempt = 0; attempt < 5 && remaining is not null; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Thread.Sleep(50);
            remaining = TryDeleteRoot();
        }

        Assert.IsNull(remaining, $"失败路径也必须清理 %TEMP%（残留目录 {_root}：{remaining}）");
    }

    private string? TryDeleteRoot()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);

            return null;
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    // ── F1：盘根键统一 ────────────────────────────────────────────────────

    [TestMethod]
    public void F1_Drive_Root_Yields_The_Same_ScopeKey_And_The_Same_Lease_File_On_Both_Paths()
    {
        var options = new FullTextIndexOptions { IndexRootDirectory = _indexRoot };
        var driveRoot = Path.GetPathRoot(Path.GetTempPath())!;

        Assert.AreEqual('\\', driveRoot[^1], $"前置：系统盘真值必须以分隔符结尾（盘根形态），实际 {driveRoot}");

        var supply = SupplyScopeNormalizer.Normalize(new[] { driveRoot });
        Assert.AreEqual(1, supply.Accepted.Count, $"供给侧必须接受盘根 scope（拒绝记录：{DescribeRejections(supply)}）");

        var supplyKey = supply.Accepted[0].ScopeKey;
        var maintenanceKey = FullTextChangeCoalescer.NormalizeComparisonKey(driveRoot);

        Assert.AreEqual(
            driveRoot.ToLowerInvariant(),
            supplyKey,
            $"供给侧对盘根必须**保盘根**（不得裁成 {driveRoot.TrimEnd('\\')}）");
        Assert.AreEqual(
            supplyKey,
            maintenanceKey,
            $"★ F1：盘根下两侧键必须逐字符相同（供给侧 {supplyKey} vs 维护侧 {maintenanceKey}）");
        Assert.AreEqual(
            FileSupplyLease.ResolveLeaseFilePath(options, supplyKey),
            FileSupplyLease.ResolveLeaseFilePath(options, maintenanceKey),
            "★ F1：盘根下两侧必须推出同一个租约文件（互斥的承载面；只算路径，不写租约文件）");

        TestContext.WriteLine($"F1 driveRoot={driveRoot} supplyKey={supplyKey} maintenanceKey={maintenanceKey}");
    }

    // ── F2：前缀判定矩阵 ──────────────────────────────────────────────────

    [TestMethod]
    [DataRow(@"c:\", @"c:\foo", true, "盘根作为祖先")]
    [DataRow(@"c:\", @"c:\", true, "盘根自身（相同）")]
    [DataRow(@"c:\foo", @"c:\foobar", false, "兄弟前缀不得误判为后代")]
    [DataRow(@"c:\foo", @"c:\foo\bar", true, "非盘根后代")]
    [DataRow(@"c:\foo", @"c:\foo", true, "非盘根自身")]
    [DataRow(@"c:\foo\bar", @"c:\foo", false, "方向反了不得成立")]
    [DataRow(@"c:\", @"c:\a\b", true, "盘根下更深层")]
    [DataRow(@"c:\a", @"d:\a", false, "跨盘不得成立")]
    public void F2_Ancestor_Or_Same_Matrix(string ancestor, string descendant, bool expected, string why)
    {
        var actual = SupplyScopeNormalizer.IsAncestorOrSame(ancestor, descendant);

        Assert.AreEqual(
            expected,
            actual,
            $"F2「{why}」：IsAncestorOrSame({ancestor}, {descendant}) 期望 {expected}，实际 {actual}");

        TestContext.WriteLine($"F2 {ancestor} vs {descendant} => {actual} ({why})");
    }

    // ── F3：单一真源（文本级 + 正/负对照）────────────────────────────────

    [TestMethod]
    public void F3_Ancestry_Judgement_Has_Exactly_One_Definition_In_The_Component()
    {
        var componentRoot = Path.Combine(RepositoryRoot(), "Source", "PuddingFullTextIndex");
        Assert.IsTrue(Directory.Exists(componentRoot), $"仪器必须能读到组件源码根：{componentRoot}");

        var files = Directory
            .EnumerateFiles(componentRoot, "*.cs", SearchOption.AllDirectories)
            .Where(p => !IsBuildArtifact(p))
            .ToArray();

        Assert.IsTrue(files.Length > 0, $"仪器必须枚举到组件源码文件（实际 {files.Length}）");

        // 正对照（同一仪器、同一文件集）：已知存在的真源签名必须命中 ⇒ 证明仪器不是「恒 0 命中」
        var positiveControl = CountOccurrences(files, "internal static string ToScopeKey(");
        Assert.IsTrue(
            positiveControl > 0,
            "正对照：仪器必须命中组件内已知存在的签名；实际 0 ⇒ 是**仪器坏了**，不是结论变了");

        // 负对照：一个哨兵 token 必须 0 命中 ⇒ 证明仪器不是「恒命中」
        Assert.AreEqual(
            0,
            CountOccurrences(files, "S3E_ABSENT_TOKEN_SENTINEL"),
            "负对照：不存在的 token 不得命中");

        Assert.AreEqual(
            1,
            CountOccurrences(files, "bool IsAncestorOrSame("),
            "★ F3：组件内「祖先或相同」判定只能有 **1** 处定义（两份重复实现是缺陷①同族的根因）");
        Assert.AreEqual(
            1,
            CountOccurrences(files, "bool IsWithinScopeKey("),
            "★ F3：组件内「scope 内」判定只能有 **1** 处定义");
        Assert.AreEqual(
            1,
            CountOccurrences(files, "static string TrimTrailingSeparators("),
            "★ F3：组件内「保盘根裁剪」只能有 **1** 处定义");

        TestContext.WriteLine($"F3 files={files.Length} positiveControl={positiveControl}");
    }

    // ── F4：行为级互斥（默认身份，本片核心）──────────────────────────────

    /// <summary>
    /// 用**两条真实路径**（真实 <see cref="FileSupplyLease"/> + 真实协调器 + 真实内核）、
    /// **都用各自角色下的默认 owner**（不再手写 <c>OwnerId</c>）：
    /// 供给侧持租约 ⇒ 维护内核必须 <c>Busy</c>（未写、未提交、0 次 reader 失效）；释放后同一变更集 <c>Applied</c>。
    /// </summary>
    [TestMethod]
    public async Task F4_Default_Owners_Make_Supply_Hold_Block_Maintenance_With_Busy()
    {
        var options = new FullTextIndexOptions { IndexRootDirectory = _indexRoot };
        var corpusFile = Path.Combine(_corpus, "a.txt");
        File.WriteAllText(corpusFile, "cross path exclusion fix marker zzfixmarker");

        // 先做一次全量构建，让 live 索引目录存在（维护侧的守卫 B 要求 live 索引已存在）
        using (var buildSearch = new LuceneSearchEngine(options))
        {
            var build = await buildSearch.BuildIndexAsync(_corpus);
            Assert.IsTrue(build.Success, $"初始全量构建必须成功：{build.Error}");
        }

        // 前置（诊断，不在此断言）：两条路径的**默认** owner 因角色而不同。
        // 「默认 owner 因角色而不同」这条**身份**断言由 M4c 持有（同进程默认身份 → 互斥）；
        // 本用例专注于**行为**：若身份相同（变异 M-F2），下面的 Busy 会变成 Applied。
        TestContext.WriteLine(
            $"F4 owners maintenance={SupplyLeaseOwner.ForCurrentProcess(SupplyLeaseRole.Maintenance).OwnerId} "
            + $"supply={new SupplyCoordinatorOptions().OwnerId}");

        // 供给路径：真实协调器 + 真实文件租约 + **默认 owner**（角色 = Supply）
        var builderStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBuilder = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = new StubSupplyBuilder(async (_, ct) =>
        {
            builderStarted.TrySetResult();
            await releaseBuilder.Task.WaitAsync(ct).ConfigureAwait(false);
            return SupplyTestHelpers.Success();
        });

        var coordinator = new FullTextIndexSupplyCoordinator(
            new StubSupplyInventory(),
            builder,
            new FileSupplyLease(options),
            new SupplyCoordinatorOptions { MinRebuildInterval = TimeSpan.Zero });

        var outcome = await coordinator.BuildAsync(new SupplyScopeRequest(_corpus));
        var scopeOutcome = SupplyTestHelpers.SingleScopeOutcome(outcome);
        Assert.AreEqual(SupplyOutcome.Started, scopeOutcome.Outcome, scopeOutcome.Reason);
        await builderStarted.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        // 维护路径：真实内核 + 真实文件租约 + **默认 owner**（角色 = Maintenance）
        var invalidations = new CountingInvalidation();
        using var maintenanceSearch = new LuceneSearchEngine(options);
        var engine = new LuceneFullTextIndexMaintenanceEngine(
            maintenanceSearch,
            options,
            new FileSupplyLease(options),
            TimeSpan.FromMilliseconds(200),
            invalidations);

        var changeSet = new FullTextChangeSet(
            "01K-FIX-BATCH-01",
            _corpus,
            FullTextChangeCoalescer.NormalizeComparisonKey(_corpus),
            new[] { Upsert(corpusFile) },
            DateTimeOffset.UtcNow,
            PreviousWatermarkUtc: null,
            RequiresCheckpointAdvance: true);

        var result = await engine.ApplyChangesAsync(changeSet, GenerousBudget(options));

        Assert.AreEqual(
            FullTextMutationState.Busy,
            result.State,
            $"★ F4：供给侧持租约时，维护路径必须 Busy（不写、不提交），实际 {result.State}：{result.Message}");
        Assert.IsNull(result.CommitMilliseconds, "Busy ⇒ 不得提交");
        Assert.IsFalse(result.CheckpointAdvanced, "Busy ⇒ 不得推进 checkpoint");
        Assert.AreEqual(0, invalidations.Count, "Busy ⇒ 0 次 reader 失效");

        // 收尾：让供给 job 正常结束，释放租约（不留下悬挂持有者）
        releaseBuilder.TrySetResult();
        await SupplyTestHelpers.AwaitJobAsync(coordinator, scopeOutcome.JobId!).ConfigureAwait(false);

        // 正对照：租约释放后，同一变更集必须能真正应用（证明上面的 Busy 不是恒真）
        var replay = await engine.ApplyChangesAsync(changeSet, GenerousBudget(options));
        Assert.AreEqual(
            FullTextMutationState.Applied,
            replay.State,
            $"★ F4 正对照：租约释放后同一变更集必须成功：{replay.Message}");
        Assert.IsNotNull(replay.CommitMilliseconds, "Applied 且真的写了 ⇒ 必须有 commit 耗时");

        TestContext.WriteLine($"F4 supplyJob={scopeOutcome.JobId} busyMessage={result.Message}");
    }

    // ── F5：同角色跨批次重入仍成立（防过度修复）──────────────────────────

    [TestMethod]
    public async Task F5_Same_Role_Same_Process_Still_Reenters_Across_Batches_Without_Resetting_Start()
    {
        var options = new FullTextIndexOptions { IndexRootDirectory = _indexRoot };
        var t0 = new DateTimeOffset(2026, 9, 26, 3, 0, 0, TimeSpan.Zero);
        var now = t0;
        var lease = new FileSupplyLease(options, utcNow: () => now);
        var owner = SupplyLeaseOwner.ForCurrentProcess(SupplyLeaseRole.Maintenance);

        var first = await lease.TryAcquireAsync("c:\\fix-scope-f5", owner, "batch-01");
        Assert.IsTrue(first.Acquired, first.Message);
        Assert.AreEqual(t0, first.Lease!.StartedAtUtc, "首次取得：起始时间 = 时钟");
        Assert.IsNull(first.Lease!.TakeoverReason, "首次取得不是接管");

        now = t0.AddSeconds(30);

        var second = await lease.TryAcquireAsync("c:\\fix-scope-f5", owner, "batch-02");
        Assert.IsTrue(second.Acquired, $"同一角色同进程跨批次必须仍可重入：{second.Message}");
        Assert.AreEqual("batch-02", second.Lease!.JobId, "重入必须更新 job 归属（可查「谁在跑哪个 job」）");
        Assert.AreEqual(
            t0,
            second.Lease!.StartedAtUtc,
            $"★ F5：重入**不得**重置起始时间（时钟已推进到 {now:O}，起始时间仍必须是 {t0:O}）");
        Assert.AreEqual(now, second.Lease!.HeartbeatUtc, "重入必须刷新心跳");
        Assert.IsNull(second.Lease!.TakeoverReason, "重入不是接管 ⇒ 不得有接管原因");
        Assert.IsNull(second.Lease!.PreviousOwnerId, "重入不是接管 ⇒ 不得记录上一位持有者");

        TestContext.WriteLine($"F5 owner={owner.OwnerId} first={first.Lease!.StartedAtUtc:O} second={second.Lease!.StartedAtUtc:O}");
    }

    // ── F6：盘根 scope 的越界判定 ────────────────────────────────────────

    [TestMethod]
    public void F6_Drive_Root_Scope_Contains_Its_Descendants_But_Not_Itself()
    {
        var driveRoot = Path.GetPathRoot(Path.GetTempPath())!;
        var child = Path.Combine(driveRoot, "pudding-f6-probe", "child.txt");

        Assert.IsTrue(
            FullTextChangeCoalescer.IsWithinScope(driveRoot, child),
            $"★ F6：盘根 scope 下的子文件必须在 scope 内（盘根键以分隔符结尾，前缀判定不得拼成 c:\\\\）：{child}");
        Assert.IsFalse(
            FullTextChangeCoalescer.IsWithinScope(driveRoot, driveRoot),
            "保持既有语义：**不含** scope 根自身（根是目录，不可能是文件变更对象）");

        // 负对照（防空转）：另一个盘的路径必须不在盘根 scope 之内
        var otherDrive = char.ToUpperInvariant(driveRoot[0]) == 'C' ? @"D:\" : @"C:\";
        Assert.IsFalse(
            FullTextChangeCoalescer.IsWithinScope(driveRoot, Path.Combine(otherDrive, "x.txt")),
            $"负对照：{otherDrive} 下的路径不得被判为 {driveRoot} scope 之内");

        TestContext.WriteLine($"F6 driveRoot={driveRoot} child={child}");
    }

    // ── 帮助 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// **不改写**给定文件：只统计文本出现次数（F3 用）。
    /// <para>返回值为 0 时调用方必须先跑正对照 —— 0 命中可能是「仪器没读到文件」而不是「真的没有」。</para>
    /// </summary>
    private static int CountOccurrences(IEnumerable<string> files, string token)
    {
        var total = 0;
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            var index = 0;
            while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
            {
                total++;
                index += token.Length;
            }
        }

        return total;
    }

    /// <summary>构建产物目录（<c>bin</c> / <c>obj</c>）不算「组件源码」。</summary>
    private static bool IsBuildArtifact(string path)
    {
        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Contains("bin", StringComparer.OrdinalIgnoreCase)
            || segments.Contains("obj", StringComparer.OrdinalIgnoreCase);
    }

    private static string DescribeRejections(SupplyScopeNormalizer.NormalizationResult result) =>
        result.Rejections.Count == 0
            ? "(none)"
            : string.Join(" | ", result.Rejections.Select(r => $"{r.Reason}:{r.Value}:{r.Message}"));

    private FullTextMutationBudget GenerousBudget(FullTextIndexOptions options) =>
        new(
            MaxIndexBytes: DefaultMaxIndexBytes,
            LiveIndexBytes: SupplyIndexDirectoryLayout.MeasureLiveIndexBytes(options.IndexRootDirectory),
            MaxPaths: DefaultMaxPaths);

    private static FullTextFileChange Upsert(string fullPath) =>
        new(fullPath, FullTextChangeKind.Upsert, LastWriteUtc: null, Length: null, Sources: FullTextChangeSource.Watcher);

    /// <summary>从测试程序集向上找仓库根（判据：<c>PuddingAgentNetwork.slnx</c>）。</summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && directory is not null; i++, directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PuddingAgentNetwork.slnx")))
                return directory.FullName;
        }

        Assert.Fail("cannot locate the repository root above " + AppContext.BaseDirectory);
        return string.Empty;
    }

    /// <summary>只计数、**不转发**的失效接缝（F4 断言「Busy ⇒ 0 次失效」）。</summary>
    private sealed class CountingInvalidation : IScopeReaderInvalidation
    {
        private int _count;

        internal int Count => Volatile.Read(ref _count);

        public void InvalidateScope(string scopeRoot) => Interlocked.Increment(ref _count);
    }
}
