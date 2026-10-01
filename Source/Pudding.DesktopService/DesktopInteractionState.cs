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
