using Pudding.Contracts.Desktop;

namespace Pudding.DesktopService;

/// <summary>自动化是否被用户/工具运行时中断。两个轴相互独立（暂停与接管可以同时成立）。</summary>
public sealed class DesktopInteractionState
{
    private readonly object _sync = new();
    private bool _paused;
    private string? _pauseReason;
    private bool _userTakeover;
    private string? _takeoverReason;

    /// <summary>当前交互持有者（单窗口同时最多一个）。</summary>
    private string? _interactionOwner;

    public bool IsPaused
    {
        get
        {
            lock (_sync)
            {
                return _paused;
            }
        }
    }

    public bool IsUserTakeover
    {
        get
        {
            lock (_sync)
            {
                return _userTakeover;
            }
        }
    }

    public string? Reason
    {
        get
        {
            lock (_sync)
            {
                return _userTakeover ? _takeoverReason : _pauseReason;
            }
        }
    }

    /// <summary>工具运行时被暂停（用户或准入策略）。</summary>
    /// <summary>
    /// 交互槽位（计划 §7：单窗口同时最多一个对话框/Picker）。
    /// 第二个并发交互必须被**拒绝**，而不是排队——排队会让用户面对"对话框序列"，
    /// 且让取消语义变得不可判定（到底取消了哪一个）。
    /// </summary>
    public bool TryEnterInteraction(string owner)
    {
        if (string.IsNullOrWhiteSpace(owner))
        {
            throw new ArgumentException("Interaction owner is required.", nameof(owner));
        }

        lock (_sync)
        {
            if (_interactionOwner is not null)
            {
                // 已有交互在途：包括同一个 owner 的重复进入（调用方 bug，但不破坏既有状态）。
                return false;
            }

            _interactionOwner = owner;
            return true;
        }
    }

    /// <summary>释放交互槽位；只有当前持有者能释放（非持有者调用被忽略，返回 false）。</summary>
    public bool ExitInteraction(string owner)
    {
        lock (_sync)
        {
            if (_interactionOwner is null || !string.Equals(_interactionOwner, owner, StringComparison.Ordinal))
            {
                return false;
            }

            _interactionOwner = null;
            return true;
        }
    }

    /// <summary>当前交互持有者；无交互时为 <c>null</c>。</summary>
    public string? InteractionOwner
    {
        get
        {
            lock (_sync)
            {
                return _interactionOwner;
            }
        }
    }

    public bool IsInteractionActive => InteractionOwner is not null;
    public void Pause(string reason)
    {
        lock (_sync)
        {
            _paused = true;
            _pauseReason = Normalize(reason);
        }
    }

    public void Resume()
    {
        lock (_sync)
        {
            _paused = false;
            _pauseReason = null;
        }
    }

    /// <summary>用户接管了浏览器：自动化必须立即停止（已发出的 UI 调用由 deadline/取消收尾）。</summary>
    public void NotifyUserTakeover(string reason)
    {
        lock (_sync)
        {
            _userTakeover = true;
            _takeoverReason = Normalize(reason);
        }
    }

    public void ClearUserTakeover()
    {
        lock (_sync)
        {
            _userTakeover = false;
            _takeoverReason = null;
        }
    }

    private static string Normalize(string? reason) =>
        string.IsNullOrWhiteSpace(reason) ? "unspecified" : reason.Trim();
}
