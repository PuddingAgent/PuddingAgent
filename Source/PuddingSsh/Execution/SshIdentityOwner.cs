using PuddingSsh.Contracts;

namespace PuddingSsh.Execution;

/// <summary>身份材料的所有权状态（ADR-096 D3 补充 L2）。</summary>
internal enum SshIdentityOwnership
{
    /// <summary>尚未挂上材料。</summary>
    Unattached,

    /// <summary>租约持有（业务路径尚未把它交付给 connect）。</summary>
    LeaseOwned,

    /// <summary>已交付 connect：只有 connect 的及时路径或迟到 owner 能释放。</summary>
    Connecting,

    /// <summary>会话持有（connect 成功，材料随会话资源一起释放）。</summary>
    SessionOwned,

    /// <summary>已释放（幂等终态）。</summary>
    Released,
}

/// <summary>
/// 单一 owner handle（L2）：把「谁在什么时候能释放身份材料」做成**原子且幂等**的状态转移。
/// <para>
/// 复核 F7 的缺口：原来 <c>TransferLateOwnership()</c> 只在迟到回调里置位，业务阶段超时返回时尚未置位，
/// finally 就把材料清零了——而迟到的 connect 可能还在读它。现在转移发生在**调用 connect 之前**
/// （<see cref="BeginConnect"/>），并且真正的释放只由 <see cref="Release"/> 幂等地做一次。
/// </para>
/// </summary>
internal sealed class SshIdentityOwner
{
    private readonly object _gate = new();
    private SshIdentityMaterial? _material;
    private SshIdentityOwnership _state = SshIdentityOwnership.Unattached;
    private int _released;

    public SshIdentityOwnership State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public int ReleaseCount => Volatile.Read(ref _released);

    /// <summary>挂上材料（<see cref="SshIdentityOwnership.Unattached"/> → <see cref="SshIdentityOwnership.LeaseOwned"/>）。</summary>
    public void Attach(SshIdentityMaterial material)
    {
        ArgumentNullException.ThrowIfNull(material);
        lock (_gate)
        {
            _material = material;
            _state = SshIdentityOwnership.LeaseOwned;
        }
    }

    /// <summary>
    /// 在**调用 connect 之前**调用：所有权转入 <see cref="SshIdentityOwnership.Connecting"/>。
    /// 幂等：重复调用不改变已进入的状态。
    /// </summary>
    public void BeginConnect()
    {
        lock (_gate)
        {
            if (_state is SshIdentityOwnership.LeaseOwned or SshIdentityOwnership.Unattached)
            {
                _state = SshIdentityOwnership.Connecting;
            }
        }
    }

    /// <summary>connect 成功：材料随会话资源释放（幂等）。</summary>
    public void MarkSessionOwned()
    {
        lock (_gate)
        {
            if (_state is SshIdentityOwnership.Connecting or SshIdentityOwnership.LeaseOwned)
            {
                _state = SshIdentityOwnership.SessionOwned;
            }
        }
    }

    /// <summary>幂等释放一次；终态为 <see cref="SshIdentityOwnership.Released"/>。</summary>
    public void Release()
    {
        SshIdentityMaterial? material;
        lock (_gate)
        {
            material = _material;
            _state = SshIdentityOwnership.Released;
        }

        if (Interlocked.Exchange(ref _released, 1) != 0)
        {
            return;
        }

        material?.Dispose();
    }
}
