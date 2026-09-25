using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PuddingFullTextIndex.Contracts;

namespace PuddingFullTextIndex.Infrastructure.Supply;

/// <summary>
/// Windows 跨进程 scope 租约（文件租约实现）。
/// <para>
/// <b>为什么不能用 Lucene 的 <c>write.lock</c></b>：本引擎的全量分支会<b>先删索引目录</b>，
/// 且索引目录名由内部哈希派生；把 Lucene 写锁当跨进程供给锁会在「删目录 / 换目录名」时失效。
/// 因此租约独立放在 <c>&lt;IndexRootDirectory&gt;/.supply-leases/&lt;sha256(scopeKey)&gt;.json</c>。
/// </para>
/// <para>
/// <b>互斥机制（两层）</b>：
/// ① <b>OS 级</b>：读改写临界区用 <see cref="FileShare.None"/> 打开租约文件 —— Windows 文件共享模式由内核强制，
/// 第二个进程若同时在临界区内会拿到 <see cref="IOException"/>，因此不会出现「同时判定为可以接管」的竞态；
/// ② <b>心跳级</b>：租约文件内容带 <c>HeartbeatUtc</c>，超过有效期（默认 2 分钟，可注入）即判定为
/// 「持有者已崩溃 / 卡死」，允许接管并把<b>接管原因</b>写入文件（可被后续进程与运维读到）。
/// </para>
/// <para>
/// ⚠️ A1 边界：租约保证「同一 scope 同时只有一个 writer」，但<b>不做</b> staging / 原子切换；
/// <c>ReleaseAsync</c> 的 close→delete 之间存在极小 TOCTOU 窗口（见方法注释），硬保证属 A2。
/// </para>
/// </summary>
public sealed class FileSupplyLease : IFullTextSupplyLease
{
    /// <summary>租约目录名（位于 <see cref="FullTextIndexOptions.IndexRootDirectory"/> 下）。</summary>
    internal const string LeaseDirectoryName = ".supply-leases";

    private static readonly JsonSerializerOptions DocumentJsonOptions = new() { WriteIndented = true };

    private readonly string _leaseDirectory;
    private readonly TimeSpan _validity;
    private readonly Func<DateTimeOffset> _utcNow;

    /// <summary>默认租约有效期（过期即可被接管）。</summary>
    public static TimeSpan DefaultLeaseValidity { get; } = TimeSpan.FromMinutes(2);

    /// <param name="options">组件配置（只取 <see cref="FullTextIndexOptions.IndexRootDirectory"/>）。</param>
    /// <param name="leaseValidity">过期判定阈值；默认 <see cref="DefaultLeaseValidity"/>（2 分钟）。</param>
    /// <param name="utcNow">可注入时钟（测试用）；默认 <see cref="DateTimeOffset.UtcNow"/>。</param>
    public FileSupplyLease(
        FullTextIndexOptions options,
        TimeSpan? leaseValidity = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.IndexRootDirectory))
            throw new ArgumentException("IndexRootDirectory 不能为空。", nameof(options));

        _leaseDirectory = Path.Combine(options.IndexRootDirectory, LeaseDirectoryName);
        _validity = leaseValidity ?? DefaultLeaseValidity;
        if (_validity <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(leaseValidity), "租约有效期必须为正。");

        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>租约目录（诊断用）。</summary>
    internal string LeaseDirectory => _leaseDirectory;

    /// <summary>给定 scope 键的租约文件路径（<c>&lt;IndexRootDirectory&gt;/.supply-leases/&lt;sha256(scopeKey)&gt;.json</c>）。</summary>
    internal static string ResolveLeaseFilePath(FullTextIndexOptions options, string scopeKey) =>
        Path.Combine(
            options.IndexRootDirectory,
            LeaseDirectoryName,
            ToLeaseFileName(scopeKey));

    internal static string ToLeaseFileName(string scopeKey) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(scopeKey))) + ".json";

    public Task<SupplyLeaseAcquireResult> TryAcquireAsync(
        string scopeKey,
        SupplyLeaseOwner owner,
        string? jobId = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        ArgumentNullException.ThrowIfNull(owner);
        ct.ThrowIfCancellationRequested();

        return Task.FromResult(Acquire(scopeKey, owner, jobId));
    }

    public Task<bool> RenewAsync(string scopeKey, string ownerId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        ct.ThrowIfCancellationRequested();

        var path = Path.Combine(_leaseDirectory, ToLeaseFileName(scopeKey));
        if (!File.Exists(path))
            return Task.FromResult(false);

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var existing = ReadDocument(stream, out _);
            if (existing is null || !string.Equals(existing.OwnerId, ownerId, StringComparison.Ordinal))
                return Task.FromResult(false);

            WriteDocument(stream, existing with { HeartbeatUtc = _utcNow() });
            return Task.FromResult(true);
        }
        catch (IOException)
        {
            return Task.FromResult(false);
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult(false);
        }
    }

    public Task<bool> ReleaseAsync(string scopeKey, string ownerId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        ct.ThrowIfCancellationRequested();

        var path = Path.Combine(_leaseDirectory, ToLeaseFileName(scopeKey));
        if (!File.Exists(path))
            return Task.FromResult(true);

        try
        {
            bool ownLease;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var existing = ReadDocument(stream, out _);
                ownLease = existing is null || string.Equals(existing.OwnerId, ownerId, StringComparison.Ordinal);
            }

            if (!ownLease)
                return Task.FromResult(false);

            // ⚠️ Windows 上被 FileShare.None 句柄持有的文件无法删除 ⇒ 必须先在上一句 using 里关闭句柄。
            // close→delete 之间有一个极小的 TOCTOU 窗口（理论上可能删掉刚被他人接管的租约）：
            // A1 如实登记为已知边界，硬保证（staging + 成功后原子切换）属 A2。
            File.Delete(path);
            return Task.FromResult(true);
        }
        catch (IOException)
        {
            return Task.FromResult(false);
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult(false);
        }
    }

    public Task<SupplyLeaseHolder?> DescribeHolderAsync(string scopeKey, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(TryDescribeHolder(scopeKey));
    }

    private SupplyLeaseAcquireResult Acquire(string scopeKey, SupplyLeaseOwner owner, string? jobId)
    {
        Directory.CreateDirectory(_leaseDirectory);
        var path = Path.Combine(_leaseDirectory, ToLeaseFileName(scopeKey));

        var existedBefore = File.Exists(path);

        FileStream stream;
        try
        {
            // 不存在 ⇒ CreateNew（原子创建，能当场撞出并发创建）；已存在 ⇒ Open。
            // **不用 OpenOrCreate**：它会「先建空文件再读」，使「刚建的空文件」与「上次写入被中断的旧文件」
            // 不可区分，从而把一次正常的首次取得误报成接管（本切片实测踩到过，见 A1 报告）。
            stream = existedBefore
                ? new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
                : new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex) when (!existedBefore)
        {
            return new SupplyLeaseAcquireResult(
                false,
                null,
                TryDescribeHolder(scopeKey),
                $"租约文件刚被其他进程创建（{ex.GetType().Name}），本次不接管、不构建。");
        }
        catch (IOException ex)
        {
            return new SupplyLeaseAcquireResult(
                false,
                null,
                TryDescribeHolder(scopeKey),
                $"租约文件正被其他进程独占（{ex.GetType().Name}），本次不接管、不构建。");
        }
        catch (UnauthorizedAccessException ex)
        {
            return new SupplyLeaseAcquireResult(
                false,
                null,
                TryDescribeHolder(scopeKey),
                $"租约文件不可写（{ex.GetType().Name}），本次不接管、不构建。");
        }

        try
        {
            var existing = ReadDocument(stream, out var readError);
            var now = _utcNow();
            string? takeoverReason = null;
            string? previousOwnerId = null;

            if (existing is not null && !string.Equals(existing.OwnerId, owner.OwnerId, StringComparison.Ordinal))
            {
                var age = now - existing.HeartbeatUtc;
                if (age < _validity)
                {
                    var holder = ToHolder(existing);
                    return new SupplyLeaseAcquireResult(
                        false,
                        null,
                        holder,
                        $"scope 已被 {holder.OwnerId}（pid={holder.ProcessId} @ {holder.MachineName}，自 {holder.StartedAtUtc:O} 起）"
                        + $"持有有效租约：最近心跳 {holder.HeartbeatUtc:O}，距现在 {age.TotalSeconds:F0}s < 有效期 {_validity.TotalSeconds:F0}s，本次不接管。");
                }

                previousOwnerId = existing.OwnerId;
                takeoverReason =
                    $"接管过期租约：原 owner={existing.OwnerId} pid={existing.ProcessId} @ {existing.MachineName}，"
                    + $"最近心跳 {existing.HeartbeatUtc:O}，距现在 {age.TotalSeconds:F0}s ≥ 有效期 {_validity.TotalSeconds:F0}s。";
            }
            else if (existing is null && existedBefore && readError is not null)
            {
                // 文件在本次调用之前就存在、但内容不可用 ⇒ 这才是接管（例如上次写入被中断）。
                // 首次取得（existedBefore == false）时刚建的空文件不算接管。
                takeoverReason = $"接管不可用租约文件：{readError}";
            }

            var startedAt = existing is not null && string.Equals(existing.OwnerId, owner.OwnerId, StringComparison.Ordinal)
                ? existing.StartedAtUtc
                : now;

            var document = new SupplyLeaseDocument(
                scopeKey,
                owner.OwnerId,
                owner.ProcessId,
                owner.MachineName,
                jobId,
                startedAt,
                now,
                previousOwnerId,
                takeoverReason);

            WriteDocument(stream, document);
            return new SupplyLeaseAcquireResult(
                true,
                ToLease(document),
                null,
                takeoverReason ?? "已取得 scope 租约。");
        }
        finally
        {
            stream.Dispose();
        }
    }

    private SupplyLeaseHolder? TryDescribeHolder(string scopeKey)
    {
        var path = Path.Combine(_leaseDirectory, ToLeaseFileName(scopeKey));
        if (!File.Exists(path))
            return null;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var document = ReadDocument(stream, out _);
            return document is null ? null : ToHolder(document);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private SupplyLeaseHolder ToHolder(SupplyLeaseDocument document) => new(
        document.OwnerId,
        document.ProcessId,
        document.MachineName,
        document.StartedAtUtc,
        document.HeartbeatUtc,
        document.JobId,
        IsExpired: _utcNow() - document.HeartbeatUtc >= _validity,
        document.TakeoverReason);

    private static SupplyLease ToLease(SupplyLeaseDocument document) => new(
        document.ScopeKey,
        new SupplyLeaseOwner(document.OwnerId, document.ProcessId, document.MachineName),
        document.JobId ?? string.Empty,
        document.StartedAtUtc,
        document.HeartbeatUtc,
        document.TakeoverReason,
        document.PreviousOwnerId);

    private static SupplyLeaseDocument? ReadDocument(Stream stream, out string? error)
    {
        error = null;
        try
        {
            stream.Position = 0;
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true);
            var text = reader.ReadToEnd();
            if (string.IsNullOrWhiteSpace(text))
            {
                error = "租约文件为空（可能上次写入被中断）";
                return null;
            }

            var document = JsonSerializer.Deserialize<SupplyLeaseDocument>(text, DocumentJsonOptions);
            if (document is null)
            {
                error = "租约文件内容不可解析（反序列化结果为 null）";
                return null;
            }

            return document;
        }
        catch (JsonException ex)
        {
            error = $"租约文件不是合法 JSON（{ex.GetType().Name}）";
            return null;
        }
        catch (IOException ex)
        {
            error = $"读租约文件失败（{ex.GetType().Name}）";
            return null;
        }
    }

    private static void WriteDocument(FileStream stream, SupplyLeaseDocument document)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(document, DocumentJsonOptions);
        stream.SetLength(0);
        stream.Position = 0;
        stream.Write(payload, 0, payload.Length);
        stream.Flush(flushToDisk: true);
    }
}
