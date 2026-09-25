using System.Collections.Concurrent;
using PuddingFullTextIndex;
using PuddingFullTextIndex.Contracts;
using PuddingFullTextIndex.Infrastructure.Supply;

namespace PuddingFullTextIndexTests;

/// <summary>
/// 供给测试的临时目录夹具。
/// <para>
/// ⚠️ 硬护栏：所有供给测试**只能**在 <see cref="Path.GetTempPath"/> 下工作 ——
/// 构造函数里直接断言，避免任何用例误指真实索引根（<c>D:\data\fulltext-index</c>）。
/// 索引根本身<b>不</b>预创建（Plan 的「零写入」断言需要它从「不存在」开始）。
/// </para>
/// </summary>
internal sealed class TempSupplyFixture : IDisposable
{
    internal TempSupplyFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "pudding-fts-supply-" + Guid.NewGuid().ToString("N"));
        Corpus = Path.Combine(Root, "corpus");
        IndexRoot = Path.Combine(Root, "index");
        Directory.CreateDirectory(Corpus);

        if (!Root.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"供给测试只允许使用系统临时目录，实际为 {Root}");

        Options = new FullTextIndexOptions { IndexRootDirectory = IndexRoot };
        ScopeKey = SupplyScopeNormalizer.ToScopeKey(Corpus);
    }

    internal string Root { get; }

    internal string Corpus { get; }

    internal string IndexRoot { get; }

    internal FullTextIndexOptions Options { get; }

    /// <summary>语料目录的规范化 scope 键（与协调器口径一致）。</summary>
    internal string ScopeKey { get; }

    internal string LeaseDirectory => Path.Combine(IndexRoot, FileSupplyLease.LeaseDirectoryName);

    internal string Write(string relativePath, string content)
    {
        var path = Path.Combine(Corpus, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    internal string CreateDirectory(string relativePath)
    {
        var path = Path.Combine(Corpus, relativePath);
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
        catch (IOException)
        {
            // 临时目录清理失败不应把测试判红
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>语料清点替身（计数确定化；不触盘）。</summary>
internal sealed class StubSupplyInventory : IFullTextIndexSupplyInventory
{
    private readonly SupplyInventory _inventory;
    private int _measureCalls;

    internal StubSupplyInventory(int fileCount = 3, long totalBytes = 4096)
    {
        _inventory = new SupplyInventory(
            fileCount,
            totalBytes,
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase) { [".cs"] = totalBytes });
    }

    internal int MeasureCallCount => Volatile.Read(ref _measureCalls);

    internal ConcurrentQueue<string> MeasuredRoots { get; } = new();

    public Task<SupplyInventory> MeasureAsync(string rootPath, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _measureCalls);
        MeasuredRoots.Enqueue(rootPath);
        return Task.FromResult(_inventory);
    }
}

/// <summary>索引构建替身：记录调用次数/scope，行为可注入（阻塞、抛异常、返回失败…）。</summary>
internal sealed class StubSupplyBuilder : IFullTextIndexBuilder
{
    private readonly Func<SupplyScope, CancellationToken, Task<SupplyBuildResult>> _behaviour;
    private readonly ConcurrentQueue<string> _scopeKeys = new();
    private int _buildCalls;

    internal StubSupplyBuilder(Func<SupplyScope, CancellationToken, Task<SupplyBuildResult>>? behaviour = null)
    {
        _behaviour = behaviour ?? ((_, _) => Task.FromResult(SupplyTestHelpers.Success()));
    }

    internal int BuildCallCount => Volatile.Read(ref _buildCalls);

    internal IReadOnlyList<string> BuiltScopeKeys => _scopeKeys.ToArray();

    public Task<SupplyBuildResult> BuildAsync(SupplyScope scope, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _buildCalls);
        _scopeKeys.Enqueue(scope.ScopeKey);
        return _behaviour(scope, ct);
    }
}

/// <summary>
/// 租约装饰器：把「第一次取租约」挂起，用于制造**真正**的闸门争用
/// （否则同 scope 的并发提交会在调用方线程上同步跑完第一条，测不出并发语义）。
/// </summary>
internal sealed class GateOnFirstAcquireLease : IFullTextSupplyLease
{
    private readonly IFullTextSupplyLease _inner;
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _calls;

    internal GateOnFirstAcquireLease(IFullTextSupplyLease inner) => _inner = inner;

    /// <summary>第一次取租约已进入（= 首个提交已握住 scope 闸门）。</summary>
    internal Task EnteredFirstAcquire => _entered.Task;

    internal void ReleaseFirstAcquire() => _release.TrySetResult();

    public async Task<SupplyLeaseAcquireResult> TryAcquireAsync(
        string scopeKey,
        SupplyLeaseOwner owner,
        string? jobId = null,
        CancellationToken ct = default)
    {
        if (Interlocked.Increment(ref _calls) == 1)
        {
            _entered.TrySetResult();
            await _release.Task.WaitAsync(ct);
        }

        return await _inner.TryAcquireAsync(scopeKey, owner, jobId, ct);
    }

    public Task<bool> RenewAsync(string scopeKey, string ownerId, CancellationToken ct = default) =>
        _inner.RenewAsync(scopeKey, ownerId, ct);

    public Task<bool> ReleaseAsync(string scopeKey, string ownerId, CancellationToken ct = default) =>
        _inner.ReleaseAsync(scopeKey, ownerId, ct);

    public Task<SupplyLeaseHolder?> DescribeHolderAsync(string scopeKey, CancellationToken ct = default) =>
        _inner.DescribeHolderAsync(scopeKey, ct);
}

/// <summary>租约装饰器：统计续期次数（用于验证「运行期间后台续期」确实发生）。</summary>
internal sealed class RenewCountingLease : IFullTextSupplyLease
{
    private readonly IFullTextSupplyLease _inner;
    private int _renewCalls;

    internal RenewCountingLease(IFullTextSupplyLease inner) => _inner = inner;

    internal int RenewCallCount => Volatile.Read(ref _renewCalls);

    public Task<SupplyLeaseAcquireResult> TryAcquireAsync(
        string scopeKey,
        SupplyLeaseOwner owner,
        string? jobId = null,
        CancellationToken ct = default) =>
        _inner.TryAcquireAsync(scopeKey, owner, jobId, ct);

    public Task<bool> RenewAsync(string scopeKey, string ownerId, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _renewCalls);
        return _inner.RenewAsync(scopeKey, ownerId, ct);
    }

    public Task<bool> ReleaseAsync(string scopeKey, string ownerId, CancellationToken ct = default) =>
        _inner.ReleaseAsync(scopeKey, ownerId, ct);

    public Task<SupplyLeaseHolder?> DescribeHolderAsync(string scopeKey, CancellationToken ct = default) =>
        _inner.DescribeHolderAsync(scopeKey, ct);
}

internal static class SupplyTestHelpers
{
    internal static SupplyBuildResult Success(int fileCount = 7, long totalBytes = 4096, long elapsedMs = 12) =>
        new(true, fileCount, totalBytes, elapsedMs, null);

    internal static SupplyBuildResult Failure(string error) => new(false, 0, 0, 5, error);

    /// <summary>等待 job 的后台执行任务结束（确定性；不轮询）。</summary>
    internal static async Task<SupplyJobStatus> AwaitJobAsync(FullTextIndexSupplyCoordinator coordinator, string jobId)
    {
        var worker = coordinator.GetWorkerTask(jobId)
            ?? throw new InvalidOperationException($"job {jobId} 没有后台任务（未知或被淘汰）。");

        await worker;
        return await coordinator.GetStatusAsync(jobId)
            ?? throw new InvalidOperationException($"job {jobId} 在结束后查不到状态。");
    }

    internal static SupplyScopeOutcome SingleScopeOutcome(SupplyRequestOutcome outcome) =>
        outcome.Scopes.Count == 1
            ? outcome.Scopes[0]
            : throw new InvalidOperationException($"期望单 scope 结果，实际 {outcome.Scopes.Count} 条。");

    /// <summary>递归快照目录内的全部条目（文件 + 目录；目录不存在时为空）。</summary>
    internal static IReadOnlyList<string> CaptureEntries(string directory)
    {
        var entries = new List<string>();
        if (!Directory.Exists(directory))
            return entries;

        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var file in Directory.GetFiles(current))
                entries.Add(Path.GetRelativePath(directory, file));

            foreach (var sub in Directory.GetDirectories(current))
            {
                entries.Add(Path.GetRelativePath(directory, sub) + Path.DirectorySeparatorChar);
                pending.Push(sub);
            }
        }

        entries.Sort(StringComparer.Ordinal);
        return entries;
    }
}
