namespace PuddingBrowser.Automation;

/// <summary>
/// 宿主绑定的引用登记表（设计方案 §5）。
///
/// 三条硬规则：
/// ① 身份用**宿主侧**节点键（例如 CDP <c>backendNodeId</c>）登记，`data-pudding-ref` 这类
///    页面可自写属性只能当"提示"，不能当身份；
/// ② 校验同时看**作用域**（context/page/frame，含 frame=null 的顶层）与**文档世代**，
///    因此「跨页但版本号恰好相同」也会被拒；
/// ③ 过期引用**绝不自动重绑**到同名/同角色的另一个节点（设计方案 §1.1 修正）：
///    role/name 只用于给调用方刷新提示。
///
/// 撤销是**分类**的：新文档提交/关页/进程失败作废该页全部引用；frame 导航只作废该 frame；
/// 单节点断开只作废那一条；而普通 DOM 变更与滚动**不作废任何**引用。
/// </summary>
public sealed class BrowserRefRegistry
{
    private readonly Lock _sync = new();
    private readonly Dictionary<string, BrowserRefToken> _tokens = new(StringComparer.Ordinal);
    private readonly HashSet<string> _revoked = new(StringComparer.Ordinal);
    private long _sequence;

    /// <summary>当前有效引用数。</summary>
    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _tokens.Count;
            }
        }
    }

    /// <summary>已作废引用的墓碑数（用于把「从未签发」与「已作废」区分开）。</summary>
    public int RevokedCount
    {
        get
        {
            lock (_sync)
            {
                return _revoked.Count;
            }
        }
    }

    /// <summary>某个页面键上仍有效的引用数（诊断/测试用）。</summary>
    public int CountForPage(string pageKey)
    {
        ArgumentNullException.ThrowIfNull(pageKey);

        lock (_sync)
        {
            return _tokens.Values.Count(token =>
                string.Equals(token.Scope.PageKey, pageKey, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// 签发一个引用。<paramref name="hostNodeKey"/> 是宿主侧节点身份，必填 ——
    /// 没有它就等于退回「信任页面自写属性」，设计明确禁止。
    /// </summary>
    public BrowserRefToken Issue(
        BrowserRefScope scope,
        BrowserObservationRevisions revisions,
        string hostNodeKey,
        string? roleHint = null,
        string? nameHint = null)
    {
        ArgumentNullException.ThrowIfNull(scope);

        if (string.IsNullOrWhiteSpace(hostNodeKey))
        {
            throw new ArgumentException(
                "A host-bound node key is required; page-written attributes are not an identity.",
                nameof(hostNodeKey));
        }

        lock (_sync)
        {
            // 值本身**不可解析**：所有判定都走登记表，调用方无法凭字符串猜出新身份，
            // 也无法凭 role/name 拼出一个"看起来相同"的引用。
            var value = $"ref-{++_sequence}-{Guid.NewGuid():N}";
            var token = new BrowserRefToken(
                value, scope, revisions.DocumentGeneration, revisions.ViewportRevision,
                hostNodeKey, roleHint, nameHint);

            _tokens[value] = token;
            return token;
        }
    }

    /// <summary>
    /// 校验引用对**节点身份**是否仍可用。
    /// <paramref name="hostReportsAttached"/> 是驱动侧存活校验（节点仍 connected 且属于同一文档）。
    /// </summary>
    public BrowserRefDecision Validate(
        string? reference,
        BrowserRefScope scope,
        BrowserObservationRevisions revisions,
        bool hostReportsAttached = true)
    {
        ArgumentNullException.ThrowIfNull(scope);

        lock (_sync)
        {
            if (!TryResolveUnsafe(reference, out var token, out var denial))
            {
                return BrowserRefDecision.Denied(denial);
            }

            if (!token!.Scope.IsSamePageAs(scope))
            {
                return BrowserRefDecision.Denied(BrowserRefDenial.PageMismatch, token);
            }

            if (!string.Equals(token.Scope.FrameId, scope.FrameId, StringComparison.Ordinal))
            {
                return BrowserRefDecision.Denied(BrowserRefDenial.FrameMismatch, token);
            }

            if (token.DocumentGeneration != revisions.DocumentGeneration)
            {
                return BrowserRefDecision.Denied(BrowserRefDenial.DocumentChanged, token);
            }

            if (!hostReportsAttached)
            {
                return BrowserRefDecision.Denied(BrowserRefDenial.NodeDetached, token);
            }

            // 视口变化**不**影响身份，只让坐标观察过期。
            return BrowserRefDecision.Valid(token, viewportStale: token.ViewportRevision != revisions.ViewportRevision);
        }
    }

    /// <summary>
    /// 校验引用能否用于**坐标**操作：除身份外还要求视口观察未过期
    /// （设计方案 §8.2：滚动/缩放/布局或页面世代改变时拒绝旧坐标）。
    /// </summary>
    public BrowserRefDecision ValidateForCoordinates(
        string? reference,
        BrowserRefScope scope,
        BrowserObservationRevisions revisions,
        bool hostReportsAttached = true)
    {
        var decision = Validate(reference, scope, revisions, hostReportsAttached);
        if (!decision.IsValid || !decision.ViewportStale)
        {
            return decision;
        }

        return BrowserRefDecision.Denied(BrowserRefDenial.ViewportMismatch, decision.Token);
    }

    /// <summary>
    /// 按类别作废引用，返回作废条数。
    ///
    /// · <see cref="BrowserRefInvalidation.DomChanged"/> / <see cref="BrowserRefInvalidation.ViewportChanged"/>
    ///   返回 <c>0</c>：普通 DOM 变更与滚动**不得**作废节点身份（设计 §5）；
    /// · 需要页面范围的类别必须给出 <paramref name="scope"/>（缺参数即调用方 bug，抛异常）；
    /// · <see cref="BrowserRefInvalidation.NodeDetached"/> 用 <see cref="RevokeRef"/>。
    /// </summary>
    public int Revoke(BrowserRefInvalidation invalidation, BrowserRefScope? scope = null)
    {
        lock (_sync)
        {
            switch (invalidation)
            {
                case BrowserRefInvalidation.DomChanged:
                case BrowserRefInvalidation.ViewportChanged:
                    // 有意 no-op：这两类变化不作废节点身份。
                    return 0;

                case BrowserRefInvalidation.NodeDetached:
                    throw new ArgumentException(
                        "A detached node invalidates exactly one ref; use RevokeRef(reference).",
                        nameof(invalidation));

                case BrowserRefInvalidation.DocumentCommitted:
                case BrowserRefInvalidation.PageClosed:
                case BrowserRefInvalidation.BrowserProcessFailed:
                {
                    // 先取 scope 再扫表：空表也要抛参数错，而不是静默返回 0。
                    var pageScope = RequireScope(scope);
                    return RevokeByUnsafe(token =>
                        string.Equals(token.Scope.PageKey, pageScope.PageKey, StringComparison.Ordinal));
                }

                case BrowserRefInvalidation.FrameNavigated:
                {
                    var frameScope = RequireScope(scope);
                    return RevokeByUnsafe(token => token.Scope.IsSameDocumentAs(frameScope));
                }

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(invalidation), invalidation, "Invalidation class is not registered.");
            }
        }
    }

    /// <summary>作废单条引用（节点被替换/移除）。返回是否命中。</summary>
    public bool RevokeRef(string? reference)
    {
        lock (_sync)
        {
            if (string.IsNullOrEmpty(reference) || !_tokens.Remove(reference))
            {
                return false;
            }

            _revoked.Add(reference);
            return true;
        }
    }

    /// <summary>清空全部引用与墓碑（浏览器进程重启）。</summary>
    public void Clear()
    {
        lock (_sync)
        {
            _tokens.Clear();
            _revoked.Clear();
        }
    }

    private bool TryResolveUnsafe(string? reference, out BrowserRefToken? token, out BrowserRefDenial denial)
    {
        token = null;

        if (string.IsNullOrWhiteSpace(reference))
        {
            denial = BrowserRefDenial.UnknownRef;
            return false;
        }

        if (_tokens.TryGetValue(reference, out var found))
        {
            token = found;
            denial = BrowserRefDenial.None;
            return true;
        }

        // 墓碑：把「已作废」与「从未签发」分开报，调用方才能给出正确提示。
        denial = _revoked.Contains(reference) ? BrowserRefDenial.Revoked : BrowserRefDenial.UnknownRef;
        return false;
    }

    private int RevokeByUnsafe(Func<BrowserRefToken, bool> predicate)
    {
        var victims = _tokens
            .Where(pair => predicate(pair.Value))
            .Select(pair => pair.Key)
            .ToArray();

        foreach (var key in victims)
        {
            _tokens.Remove(key);
            _revoked.Add(key);
        }

        return victims.Length;
    }

    private static BrowserRefScope RequireScope(BrowserRefScope? scope) =>
        scope ?? throw new ArgumentNullException(
            nameof(scope), "This invalidation class invalidates a whole page or frame; a scope is required.");
}
