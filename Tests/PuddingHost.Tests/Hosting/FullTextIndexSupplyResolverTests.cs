using PuddingHost.Hosting;

namespace PuddingHost.Tests.Hosting;

/// <summary>
/// U4-7 A1~A5：<see cref="FullTextIndexSupplyResolver"/> 的 fail-closed 校验（**纯函数**，全部走替身探针，
/// 除 A1 的「mtime 不变」需要真临时目录外不访问文件系统）。
/// </summary>
public sealed class FullTextIndexSupplyResolverTests
{
    // ── A1 ────────────────────────────────────────────────────────────────
    /// <summary>
    /// A1：默认（<c>Enabled=false</c>）⇒ 解析成功且是**空动作**，且**零 I/O** ——
    /// 连 scope 探针都不调用，伪索引根目录的 mtime / 条目集合逐位不变。
    /// </summary>
    [Fact]
    public void Disabled_Is_A_Successful_NoOp_And_Does_Not_Touch_The_Index_Directory()
    {
        var fakeIndexRoot = NewTempDirectory();
        try
        {
            // 刻意给出**非空且合法**的 Scopes：这样「关闭 ⇒ 零 I/O」才是被检验的契约，
            // 而不是「因为没有 scope 所以没事可做」。
            var options = new FullTextIndexSupplyOptions { Scopes = [fakeIndexRoot] };
            var probe = new RecordingProbe().Map(fakeIndexRoot, FullTextIndexScopePathKind.Directory);

            var writeTimeBefore = Directory.GetLastWriteTimeUtc(fakeIndexRoot);
            var entriesBefore = Directory.GetFileSystemEntries(fakeIndexRoot);

            var resolution = FullTextIndexSupplyResolver.Resolve(options, probe.Probe);

            Assert.True(resolution.Succeeded, "关闭不是错误：必须解析成功");
            Assert.True(resolution.IsNoOp, "关闭 ⇒ 空动作");
            Assert.False(resolution.Enabled);
            Assert.Empty(resolution.AcceptedScopes);
            Assert.Empty(resolution.RejectedScopes);
            Assert.Empty(resolution.Rejections);

            Assert.Equal(0, probe.Calls); // 零 I/O 的机械证明：探针一次都没被调用
            Assert.Equal(writeTimeBefore, Directory.GetLastWriteTimeUtc(fakeIndexRoot));
            Assert.Equal(entriesBefore, Directory.GetFileSystemEntries(fakeIndexRoot));
            Assert.Contains("disabled", resolution.Describe(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(fakeIndexRoot, recursive: true);
        }
    }

    // ── A4 ────────────────────────────────────────────────────────────────
    /// <summary>
    /// A4：不提供任何配置 ⇒ 默认关闭、空 scope、体积上限 = 1 GiB（2^30）、重建间隔 12h。
    /// <para>
    /// 这里**故意写死** <c>1_073_741_824</c>：若改成读
    /// <c>FullTextIndexSupplyOptions.DefaultMaxIndexBytes</c>，断言就恒真、失去「取红」能力（A4 的全部意义）。
    /// 生产代码里该字面量只出现一次（R2 单一真源）。
    /// </para>
    /// </summary>
    [Fact]
    public void Defaults_Are_Off_With_One_GiB_And_Twelve_Hour_Rebuild_Interval()
    {
        var options = new FullTextIndexSupplyOptions();

        Assert.False(options.Enabled, "默认必须是关闭：否则现网行为会变");
        Assert.Empty(options.Scopes);
        Assert.Equal(1_073_741_824L, options.MaxIndexBytes);
        Assert.Equal(TimeSpan.FromHours(12), options.MinRebuildInterval);
        Assert.Null(options.WorkspaceRoot);
        Assert.Equal("FullTextIndex", FullTextIndexSupplyOptions.SectionName);
    }

    // ── A2 ────────────────────────────────────────────────────────────────
    /// <summary>A2：<c>Enabled=true</c> 且 <c>Scopes</c> 为空 ⇒ 拒绝，原因可定位到「Scopes 为空」。</summary>
    [Fact]
    public void Enabled_With_Empty_Scopes_Is_Rejected_And_Points_At_Scopes()
    {
        var probe = new RecordingProbe();
        var options = new FullTextIndexSupplyOptions { Enabled = true };

        var resolution = FullTextIndexSupplyResolver.Resolve(options, probe.Probe);

        Assert.False(resolution.Succeeded, "「开了但没人可索引」必须显式失败，不能静默空转");
        Assert.True(resolution.Enabled);
        Assert.Empty(resolution.AcceptedScopes);
        Assert.Equal(0, probe.Calls);

        var rejection = Assert.Single(resolution.Rejections);
        Assert.Equal("Scopes", rejection.ParameterName);
        Assert.Equal(FullTextIndexSupplyRejectReason.ScopesEmpty, rejection.Reason);
        Assert.Equal("[]", rejection.Value);
        Assert.False(string.IsNullOrWhiteSpace(rejection.Message));
        Assert.Contains("Scopes", rejection.Message, StringComparison.Ordinal);
        Assert.Contains("empty", rejection.Message, StringComparison.Ordinal);
    }

    // ── A3 ────────────────────────────────────────────────────────────────
    /// <summary>
    /// A3：<c>Scopes</c> 混入「不存在 / 重复 / 非目录 / 空串」⇒ accepted 与 rejected **分别**列出，
    /// 且 rejected 逐条带原因（同一份结果里能看出每一项为什么不合格）。
    /// </summary>
    [Fact]
    public void Scopes_Are_Partitioned_Into_Accepted_And_Rejected_With_Reasons()
    {
        var existing = @"C:\u4-7-scope\exists";
        var missing = @"C:\u4-7-scope\missing";
        var aFile = @"C:\u4-7-scope\a-file.cs";

        var probe = new RecordingProbe()
            .Map(existing, FullTextIndexScopePathKind.Directory)
            .Map(aFile, FullTextIndexScopePathKind.NotDirectory);

        var options = new FullTextIndexSupplyOptions
        {
            Enabled = true,
            Scopes = [missing, existing, existing, aFile, ""],
        };

        var resolution = FullTextIndexSupplyResolver.Resolve(options, probe.Probe);

        Assert.False(resolution.Succeeded);
        Assert.Equal([Normalize(existing)], resolution.AcceptedScopes);

        Assert.Equal(
            [
                FullTextIndexSupplyRejectReason.ScopeNotFound,
                FullTextIndexSupplyRejectReason.ScopeDuplicate,
                FullTextIndexSupplyRejectReason.ScopeNotDirectory,
                FullTextIndexSupplyRejectReason.ScopeEmpty,
            ],
            resolution.RejectedScopes.Select(static r => r.Reason).ToArray());

        Assert.All(resolution.RejectedScopes, static r =>
        {
            Assert.False(string.IsNullOrWhiteSpace(r.Message));
            Assert.NotEqual(FullTextIndexSupplyRejectReason.Unknown, r.Reason);
        });

        // 逐条可定位：不存在的那条把解析后的绝对路径写进结果。
        Assert.Equal(Normalize(missing), resolution.RejectedScopes[0].ResolvedPath);
        Assert.Equal(Normalize(existing), resolution.RejectedScopes[1].ResolvedPath);
        Assert.Equal(Normalize(aFile), resolution.RejectedScopes[2].ResolvedPath);
    }

    /// <summary>相对 scope：没有绝对基准 ⇒ 拒绝（**绝不**退回进程 CWD）。</summary>
    [Fact]
    public void Relative_Scope_Without_Declared_Absolute_Base_Is_Rejected()
    {
        var probe = new RecordingProbe().Map(@"D:\ws\sub", FullTextIndexScopePathKind.Directory);
        var options = new FullTextIndexSupplyOptions { Enabled = true, Scopes = [@"sub"] };

        var resolution = FullTextIndexSupplyResolver.Resolve(options, probe.Probe);

        Assert.False(resolution.Succeeded);
        var rejection = Assert.Single(resolution.RejectedScopes);
        Assert.Equal(FullTextIndexSupplyRejectReason.ScopeRelativeBaseUnavailable, rejection.Reason);
        Assert.Contains("WorkspaceRoot", rejection.Message, StringComparison.Ordinal);
        Assert.Contains("working directory is never used", rejection.Message, StringComparison.Ordinal);
    }

    /// <summary>相对 scope：有显式绝对基准 ⇒ 以基准解析（基准来自配置，不是 CWD）。</summary>
    [Fact]
    public void Relative_Scope_Is_Resolved_Against_The_Declared_WorkspaceRoot()
    {
        const string workspaceRoot = @"D:\ws";
        var expected = Normalize(Path.Combine(workspaceRoot, "sub"));
        var probe = new RecordingProbe().Map(expected, FullTextIndexScopePathKind.Directory);
        var options = new FullTextIndexSupplyOptions
        {
            Enabled = true,
            WorkspaceRoot = workspaceRoot,
            Scopes = [@"sub"],
        };

        var resolution = FullTextIndexSupplyResolver.Resolve(options, probe.Probe);

        Assert.True(resolution.Succeeded, resolution.Describe());
        Assert.Equal([expected], resolution.AcceptedScopes);
        Assert.DoesNotContain(
            Normalize(Directory.GetCurrentDirectory()),
            resolution.AcceptedScopes[0],
            StringComparison.OrdinalIgnoreCase);
    }

    // ── A5 ────────────────────────────────────────────────────────────────
    /// <summary>
    /// A5（上界侧）：<c>MaxIndexBytes &lt;= 0</c> ⇒ 拒绝，并给出参数名与值。
    /// <para>刻意配一个**合法 scope**：这样「把 <c>&lt;= 0</c> 放宽成 <c>&lt; 0</c>」的变异才会让本用例翻红。</para>
    /// </summary>
    [Fact]
    public void NonPositive_MaxIndexBytes_Is_Rejected_With_Parameter_Name_And_Value()
    {
        var scope = @"C:\u4-7-scope\exists";
        var probe = new RecordingProbe().Map(scope, FullTextIndexScopePathKind.Directory);
        var options = new FullTextIndexSupplyOptions { Enabled = true, Scopes = [scope], MaxIndexBytes = 0 };

        var resolution = FullTextIndexSupplyResolver.Resolve(options, probe.Probe);

        Assert.False(resolution.Succeeded);
        var rejection = Assert.Single(resolution.Rejections);
        Assert.Equal("MaxIndexBytes", rejection.ParameterName);
        Assert.Equal("0", rejection.Value);
        Assert.Equal(FullTextIndexSupplyRejectReason.MaxIndexBytesNotPositive, rejection.Reason);
        Assert.Contains("MaxIndexBytes", rejection.Message, StringComparison.Ordinal);
    }

    /// <summary>A5（上限侧）：超过硬天花板（1 TiB）⇒ 拒绝。天花板取自选项类型常量，不写字面量。</summary>
    [Fact]
    public void MaxIndexBytes_Above_The_Hard_Ceiling_Is_Rejected()
    {
        var scope = @"C:\u4-7-scope\exists";
        var probe = new RecordingProbe().Map(scope, FullTextIndexScopePathKind.Directory);
        var aboveCeiling = FullTextIndexSupplyOptions.MaxAllowedIndexBytes + 1;
        var options = new FullTextIndexSupplyOptions
        {
            Enabled = true,
            Scopes = [scope],
            MaxIndexBytes = aboveCeiling,
        };

        var resolution = FullTextIndexSupplyResolver.Resolve(options, probe.Probe);

        Assert.False(resolution.Succeeded);
        var rejection = Assert.Single(resolution.Rejections);
        Assert.Equal("MaxIndexBytes", rejection.ParameterName);
        Assert.Equal(aboveCeiling.ToString(System.Globalization.CultureInfo.InvariantCulture), rejection.Value);
        Assert.Equal(FullTextIndexSupplyRejectReason.MaxIndexBytesAboveLimit, rejection.Reason);
    }

    /// <summary>A5（间隔侧）：<c>MinRebuildInterval &lt; 0</c> ⇒ 拒绝，并给出参数名与值。</summary>
    [Fact]
    public void Negative_MinRebuildInterval_Is_Rejected_With_Parameter_Name_And_Value()
    {
        var scope = @"C:\u4-7-scope\exists";
        var probe = new RecordingProbe().Map(scope, FullTextIndexScopePathKind.Directory);
        var options = new FullTextIndexSupplyOptions
        {
            Enabled = true,
            Scopes = [scope],
            MinRebuildInterval = TimeSpan.FromMinutes(-1),
        };

        var resolution = FullTextIndexSupplyResolver.Resolve(options, probe.Probe);

        Assert.False(resolution.Succeeded);
        var rejection = Assert.Single(resolution.Rejections);
        Assert.Equal("MinRebuildInterval", rejection.ParameterName);
        Assert.Equal("-00:01:00", rejection.Value);
        Assert.Equal(FullTextIndexSupplyRejectReason.MinRebuildIntervalNegative, rejection.Reason);
    }

    /// <summary>边界：<c>MinRebuildInterval = TimeSpan.Zero</c>（= 每次都重建）是**合法**的。</summary>
    [Fact]
    public void Zero_MinRebuildInterval_Is_Accepted()
    {
        var scope = @"C:\u4-7-scope\exists";
        var probe = new RecordingProbe().Map(scope, FullTextIndexScopePathKind.Directory);
        var options = new FullTextIndexSupplyOptions
        {
            Enabled = true,
            Scopes = [scope],
            MinRebuildInterval = TimeSpan.Zero,
        };

        var resolution = FullTextIndexSupplyResolver.Resolve(options, probe.Probe);

        Assert.True(resolution.Succeeded, resolution.Describe());
        Assert.Empty(resolution.Rejections);
    }

    // ── 局部工具 ──────────────────────────────────────────────────────────
    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static string NewTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "PuddingAgent", $"u4-7-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>scope 探针替身：显式映射 + 计数（用来证明「不调用」与「调用了几次」）。</summary>
    private sealed class RecordingProbe
    {
        private readonly Dictionary<string, FullTextIndexScopePathKind> _kinds =
            new(StringComparer.OrdinalIgnoreCase);

        public int Calls { get; private set; }

        public RecordingProbe Map(string path, FullTextIndexScopePathKind kind)
        {
            _kinds[path] = kind;
            return this;
        }

        public FullTextIndexScopePathKind Probe(string path)
        {
            Calls++;
            return _kinds.TryGetValue(path, out var kind) ? kind : FullTextIndexScopePathKind.Missing;
        }
    }
}
