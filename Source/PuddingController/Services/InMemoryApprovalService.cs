using System.Collections.Concurrent;
using PuddingCode.Platform;

namespace PuddingController.Services;

/// <summary>
/// 进程内审批服务——审批单为临时数据，默认 24h 过期。
/// </summary>
/// <remarks>
/// <para><b>为什么是进程内实现（2026-09-20）</b>：本类此前声明依赖
/// <c>IConnectionMultiplexer</c>（Redis），但组合根
/// （<c>PuddingController.DependencyInjection.AddPuddingController</c>）**从未注册过它**，
/// 也从未注册 Redis（全仓无 <c>AddStackExchangeRedis</c>/<c>ConnectionMultiplexer.Connect</c>
/// 调用点）。于是 <c>/api/approval/*</c> 四个端点在已认证请求下必然因 DI 无法构造
/// 而抛错、返回 <b>500</b>——即四个端点**全部是死接口**（匿名 401 由类级
/// <c>[Authorize]</c> 挡住，掩盖了这一点）。组合根自己的注释即写明
/// 「V1 最小注册（InMemory，无 PostgreSQL/Redis）」，故这里让实现与命名、
/// 与当前部署形态一致：进程内存储。</para>
///
/// <para><b>取舍</b>：进程内状态不跨进程共享，多实例部署下同一审批单在不同实例上
/// 不可见。当前为单进程部署，且审批单生命周期 ≤ 24h，因此可接受。若将来需要
/// 多实例共享，应改为 Redis/数据库实现，并**同时**补上组合根注册，
/// 以及 <see cref="ApprovalCode"/> 类文档中登记的失败尝试限制。</para>
///
/// <para><b>并发</b>：状态迁移（Pending → Confirmed / Rejected / Expired）一律经
/// <see cref="ConcurrentDictionary{TKey,TValue}.TryUpdate(TKey,TValue,TValue)"/>
/// 的比较交换完成，因此「两个请求同时确认同一单」只会有一次成功，另一次拿到
/// <c>false</c>，而不会双双成功。</para>
/// </remarks>
public sealed class InMemoryApprovalService : IApprovalService
{
    private static readonly TimeSpan DefaultExpiry = TimeSpan.FromHours(24);

    private readonly ConcurrentDictionary<string, ApprovalRecord> _records = new(StringComparer.Ordinal);

    private readonly TimeSpan _approvalExpiry;

    /// <summary>组合根使用的默认构造：审批单 24h 过期。</summary>
    public InMemoryApprovalService() : this(DefaultExpiry)
    {
    }

    /// <summary>
    /// 可指定有效期——仅供测试构造短生命周期审批单以验证回收。
    /// （<see cref="TimeSpan"/> 无法被 DI 解析，故组合根仍会选中上面的无参构造。）
    /// </summary>
    public InMemoryApprovalService(TimeSpan approvalExpiry)
    {
        _approvalExpiry = approvalExpiry;
    }

    public Task<ApprovalRecord> RequestApprovalAsync(
        string sessionId, string workspaceId, string actionDescription,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        PruneExpired(now);

        var record = new ApprovalRecord
        {
            SessionId = sessionId,
            WorkspaceId = workspaceId,
            ActionDescription = actionDescription,
            ConfirmationCode = ApprovalCode.Generate(),
            ExpiresAt = now.Add(_approvalExpiry),
        };

        _records[record.ApprovalId] = record;
        return Task.FromResult(record);
    }

    public Task<ApprovalRecord?> GetAsync(string approvalId, CancellationToken ct = default)
    {
        _records.TryGetValue(approvalId, out var record);
        return Task.FromResult(record);
    }

    /// <summary>只返回仍在 Pending 且未过期的审批单，按创建时间倒序。</summary>
    public Task<IReadOnlyList<ApprovalRecord>> QueryPendingAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        PruneExpired(now);

        IReadOnlyList<ApprovalRecord> pending = _records.Values
            .Where(r => r.Status == ApprovalStatus.Pending && !IsExpired(r, now))
            .OrderByDescending(r => r.CreatedAt)
            .ToList();

        return Task.FromResult(pending);
    }

    public Task<bool> ConfirmAsync(
        string approvalId, string confirmationCode, string confirmedBy,
        CancellationToken ct = default)
    {
        // 用 TryUpdate 的比较交换做状态迁移：只有把「仍处于当时读到的那个版本」
        // 换成 Confirmed 才返回 true，保证并发确认不会双双成功。
        if (!_records.TryGetValue(approvalId, out var record)) return Task.FromResult(false);
        if (record.Status != ApprovalStatus.Pending) return Task.FromResult(false);

        if (IsExpired(record, DateTimeOffset.UtcNow))
        {
            _records.TryUpdate(approvalId, record with { Status = ApprovalStatus.Expired }, record);
            return Task.FromResult(false);
        }

        // 失败尝试累计：达到上限即作废（Expired），持码人需重新发起审批。
        // 注意顺序——先把本次失败计入，再判上限：第 MaxFailedAttempts 次错误本身
        // 就是触发作废的那一次，而不是要等到第 MaxFailedAttempts + 1 次。
        if (!ApprovalCode.Matches(record.ConfirmationCode, confirmationCode))
        {
            var attempts = record.FailedAttempts + 1;
            var failed = ApprovalCode.IsExhausted(attempts)
                ? record with
                {
                    FailedAttempts = attempts,
                    Status = ApprovalStatus.Expired,
                    ResolvedAt = DateTimeOffset.UtcNow,
                }
                : record with { FailedAttempts = attempts };

            _records.TryUpdate(approvalId, failed, record);
            return Task.FromResult(false);
        }

        var confirmed = record with
        {
            Status = ApprovalStatus.Confirmed,
            ResolvedAt = DateTimeOffset.UtcNow,
            ResolvedBy = confirmedBy,
        };

        return Task.FromResult(_records.TryUpdate(approvalId, confirmed, record));
    }

    public Task<bool> RejectAsync(string approvalId, string rejectedBy, CancellationToken ct = default)
    {
        if (!_records.TryGetValue(approvalId, out var record)) return Task.FromResult(false);
        if (record.Status != ApprovalStatus.Pending) return Task.FromResult(false);

        var rejected = record with
        {
            Status = ApprovalStatus.Rejected,
            ResolvedAt = DateTimeOffset.UtcNow,
            ResolvedBy = rejectedBy,
        };

        return Task.FromResult(_records.TryUpdate(approvalId, rejected, record));
    }

    private static bool IsExpired(ApprovalRecord record, DateTimeOffset now)
        => record.ExpiresAt is not null && record.ExpiresAt <= now;

    /// <summary>
    /// 回收已过期记录（惰性：在写入与查询路径顺带触发，不引入后台定时器）。
    /// </summary>
    /// <remarks>
    /// 过期记录已无任何用途——不能被确认（<see cref="ConfirmAsync"/> 会先因
    /// <see cref="IsExpired"/> 拒绝）、不出现在待审列表、也不再有任何可执行的
    /// 后续动作——故过期即可回收。这里不存在「为审计而误删」的风险：本实现
    /// 本来就是进程内易失存储，进程重启即全部丢失。不回收的话，字典会随运行
    /// 时长单调增长（每张审批单永久驻留）。
    /// </remarks>
    private int PruneExpired(DateTimeOffset now)
    {
        var removed = 0;
        foreach (var kv in _records)
        {
            if (IsExpired(kv.Value, now) && _records.TryRemove(kv.Key, out _))
                removed++;
        }

        return removed;
    }
}
