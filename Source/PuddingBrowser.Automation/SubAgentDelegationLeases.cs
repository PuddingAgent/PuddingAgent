namespace PuddingBrowser.Automation;

/// <summary>租约持有者的角色（父子都必须能被单独识别，才能"接管/撤权同时失效"）。</summary>
public enum BrowserLeaseRole
{
    Parent,
    Child,
}

/// <summary>一次租约授予的结果（拒绝时给出**具体**原因，调用方不得猜）。</summary>
public sealed record BrowserLeaseOutcome(bool Granted, string? Reason)
{
    public static BrowserLeaseOutcome Allow() => new(true, null);

    public static BrowserLeaseOutcome Deny(string reason) => new(false, reason);
}

/// <summary>
/// **父子页面写租约的账本**（设计方案 §9 / 门禁 G10）：
/// <list type="bullet">
///   <item><b>同一页同一时刻只有一个写租约</b>——父子不得并存（父在写时委派必须先暂停/释放）；</item>
///   <item><b>只读观察可并行</b>（多个 reader 同一页互不阻塞）；</item>
///   <item><b>写入按页串行</b>；</item>
///   <item><b>接管/撤权使父子同时失效</b>（不是只失效子代理）；</item>
///   <item>委派结束/超时归还租约（父可以重新写）。</item>
/// </list>
///
/// 这是**策略账本**：它不做线程同步（那属于宿主），只回答"这次授予允不允许"。
/// 因此每条转换都能在进程内确定性验证。
/// </summary>
public sealed class BrowserDelegationLeaseLedger
{
    private readonly Dictionary<string, LeaseHolder> _writes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _reads = new(StringComparer.Ordinal);
    private readonly HashSet<string> _revokedPages = new(StringComparer.Ordinal);

    // 一个委派可以同时持有**多页**的租约（子代理可能在第二个标签页里继续写）。
    // 这里必须是"委派 → 页集合"：早先写成单值映射时，第二个页会覆盖第一个，
    // 于是 EndDelegation 只归还最后一页，**第一页的子代理写租约永久泄漏**（父代理再也写不进去）。
    private readonly Dictionary<string, HashSet<string>> _pagesOfDelegation = new(StringComparer.Ordinal);

    // 该委派登记过的**只读观察者 id**。只读租约也要能被"委派结束"回收：
    // 早先只按 `child:{delegationId}` 前缀扫，而子代理完全可以只拿只读观察、从不申请写租约，
    // 于是那些观察在委派结束后**永久留存在账本里**（"委派结束即不再授权"被违反）。
    // 这里记**确切的 id**（不靠字符串约定），回收时精确移除，不误伤别的观察者。
    private readonly Dictionary<string, HashSet<string>> _readersOfDelegation = new(StringComparer.Ordinal);

    private sealed record LeaseHolder(string HolderId, BrowserLeaseRole Role, string? DelegationId);

    /// <summary>当前持有该页写租约的持有者（诊断用；没有则 null）。</summary>
    public string? WriteHolderOf(string pageId) =>
        _writes.TryGetValue(pageId, out var holder) ? holder.HolderId : null;

    /// <summary>该页当前的只读观察者数量。</summary>
    public int ReaderCountOf(string pageId) => _reads.TryGetValue(pageId, out var readers) ? readers.Count : 0;

    /// <summary>该页是否已被接管/撤权（此时任何新租约都不再授予）。</summary>
    public bool IsRevoked(string pageId) => _revokedPages.Contains(pageId);

    /// <summary>
    /// 申请该页写租约。**同一页只允许一个写租约**：已有持有者（无论父还是另一个子代理）一律拒绝。
    /// </summary>
    public BrowserLeaseOutcome TryAcquireWrite(
        string pageId,
        string holderId,
        BrowserLeaseRole role,
        string? delegationId = null)
    {
        Require(pageId, nameof(pageId));
        Require(holderId, nameof(holderId));

        if (IsRevoked(pageId))
        {
            // 接管/撤权之后旧持有者不应再拿到写权（重新授权要走新的 grant）。
            return BrowserLeaseOutcome.Deny($"page '{pageId}' was taken over or revoked; a new write lease needs a new grant");
        }

        if (_writes.TryGetValue(pageId, out var existing))
        {
            if (existing.HolderId == holderId)
            {
                // 幂等：同一持有者重复申请视为已持有。
                return BrowserLeaseOutcome.Allow();
            }

            // §9：父子不能并发持有同页写租约（父必须先暂停写入）。
            return BrowserLeaseOutcome.Deny(
                $"page '{pageId}' already has a write lease held by '{(existing.Role == BrowserLeaseRole.Parent ? "parent" : "child")}'"
                + $" ('{existing.HolderId}'); the parent must pause writing before delegating");
        }

        _writes[pageId] = new LeaseHolder(holderId, role, delegationId);
        if (role == BrowserLeaseRole.Child && delegationId is not null)
        {
            RegisterPageOfDelegation(delegationId, pageId);
        }

        return BrowserLeaseOutcome.Allow();
    }

    /// <summary>
    /// 申请只读观察：**可并行**（多 reader 互不阻塞），但被接管/撤权的页不再授予。
    ///
    /// <paramref name="delegationId"/> 非空时表示"这是某个子代理委派的观察"：
    /// 该页与该观察者 id 都会登记到委派上，**委派结束（含取消/超时）时被一并回收**。
    /// 子代理请务必传它，否则委派结束后这条观察会留在账本里。
    /// </summary>
    public BrowserLeaseOutcome TryAcquireRead(string pageId, string holderId, string? delegationId = null)
    {
        Require(pageId, nameof(pageId));
        Require(holderId, nameof(holderId));

        if (IsRevoked(pageId))
        {
            return BrowserLeaseOutcome.Deny($"page '{pageId}' was taken over or revoked; observation is no longer authorized");
        }

        if (!_reads.TryGetValue(pageId, out var readers))
        {
            readers = new HashSet<string>(StringComparer.Ordinal);
            _reads[pageId] = readers;
        }

        readers.Add(holderId);

        if (delegationId is not null)
        {
            RegisterPageOfDelegation(delegationId, pageId);
            if (!_readersOfDelegation.TryGetValue(delegationId, out var readers_))
            {
                readers_ = new HashSet<string>(StringComparer.Ordinal);
                _readersOfDelegation[delegationId] = readers_;
            }

            readers_.Add(holderId);
        }

        return BrowserLeaseOutcome.Allow();
    }

    private void RegisterPageOfDelegation(string delegationId, string pageId)
    {
        if (!_pagesOfDelegation.TryGetValue(delegationId, out var pages))
        {
            pages = new HashSet<string>(StringComparer.Ordinal);
            _pagesOfDelegation[delegationId] = pages;
        }

        pages.Add(pageId);
    }

    /// <summary>释放：写租约按持有者匹配（不是自己的释放不掉）；只读按持有者移除。</summary>
    public void ReleaseWrite(string pageId, string holderId)
    {
        if (_writes.TryGetValue(pageId, out var holder) && holder.HolderId == holderId)
        {
            _writes.Remove(pageId);
        }
    }

    public void ReleaseRead(string pageId, string holderId)
    {
        if (_reads.TryGetValue(pageId, out var readers))
        {
            readers.Remove(holderId);
            if (readers.Count == 0)
            {
                _reads.Remove(pageId);
            }
        }
    }

    /// <summary>
    /// **委派结束/超时归还**：释放该委派（子代理）在该页的写租约，父代理随即可以重新写。
    /// </summary>
    public void EndDelegation(string delegationId)
    {
        Require(delegationId, nameof(delegationId));

        if (!_pagesOfDelegation.Remove(delegationId, out var pages))
        {
            return;
        }

        // 该委派**在每一页**上的写租约都必须归还（不是只归还最后登记的那一页）。
        _readersOfDelegation.Remove(delegationId, out var registeredReaders);

        foreach (var pageId in pages)
        {
            if (_writes.TryGetValue(pageId, out var holder)
                && holder.Role == BrowserLeaseRole.Child
                && holder.DelegationId == delegationId)
            {
                _writes.Remove(pageId);
            }

            // 子代理的只读观察也随之失效（委派结束即不再授权）。
            if (_reads.TryGetValue(pageId, out var readers))
            {
                // ① 精确移除本委派登记过的观察者 id；
                // ② 兼容按约定命名的 `child:{delegationId}[:...]`（约定外的调用方仍能被回收）。
                if (registeredReaders is not null)
                {
                    readers.RemoveWhere(registeredReaders.Contains);
                }

                readers.RemoveWhere(reader => reader.StartsWith($"child:{delegationId}", StringComparison.Ordinal));
                if (readers.Count == 0)
                {
                    _reads.Remove(pageId);
                }
            }
        }
    }

    /// <summary>
    /// **接管/撤权**：该页的父子写租约与只读观察**同时失效**，且此后不再授予新租约。
    /// （§9 明确"接管和撤权使父子同时失效"，不是只失效子代理。）
    /// </summary>
    public void RevokePage(string pageId)
    {
        Require(pageId, nameof(pageId));

        _revokedPages.Add(pageId);
        _writes.Remove(pageId);
        _reads.Remove(pageId);

        // 只把**这一页**从各委派的登记里摘掉：同一委派在别的页上的租约不受影响
        // （撤权是按页的，不是按委派的）。
        foreach (var delegation in _pagesOfDelegation.Keys.ToArray())
        {
            var pages = _pagesOfDelegation[delegation];
            pages.Remove(pageId);
            if (pages.Count == 0)
            {
                _pagesOfDelegation.Remove(delegation);
            }
        }
    }

    /// <summary>重新授权（新 grant）后可再次授予租约。</summary>
    public void RegrantPage(string pageId)
    {
        Require(pageId, nameof(pageId));
        _revokedPages.Remove(pageId);
    }

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{name} is required", name);
        }
    }
}
