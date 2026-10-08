using Pudding.Contracts.Desktop;
using PuddingBrowser.Automation;

namespace Pudding.DesktopService;

/// <summary>
/// 自动化是否被用户/工具运行时中断。两个轴相互独立（暂停与接管可以同时成立）。
///
/// <b>这两个轴不再是本类的私有字段</b>：接管/暂停的<b>唯一事实源</b>是进程内那一个
/// <see cref="BrowserAutomationAuthority"/>（设计方案 §3.1：「现有三份可写 takeover/paused 字段
/// 收敛为投影」）。本类只是它的读投影 + 写入口，因此 Shell 的接管开关、能力通道准入与
/// Bridge 闸门看到的必然是同一份状态，不需要靠 fan-out 维持一致。
///
/// 交互槽位（对话框/Picker 单窗口互斥）不属于控制权，仍由本类自己持有。
/// </summary>
public sealed class DesktopInteractionState
{
    private readonly object _sync = new();
    private readonly BrowserAutomationAuthority _authority;

    /// <summary>当前交互持有者（单窗口同时最多一个）。</summary>
    private string? _interactionOwner;

    /// <summary>
    /// 两轴的**原因标签**（仅供显示/诊断）。
    ///
    /// 为什么原因留在这里、标志却不在：权威只保留**一个** reason 槽位（它是"最近一次控制状态变更"的
    /// 原因），而 UI 需要"接管解除后仍显示暂停的原因"。因此本类记住两轴各自的原因，
    /// 在解除一轴时把**仍在保持的那一轴**的原因回写给权威 —— 于是
    /// <see cref="Reason"/> 始终是「当前仍在保持的那个原因」，两轴都解除时为 null。
    ///
    /// 这只是标签：任何准入/闸门判定都读 <see cref="IsPaused"/>/<see cref="IsUserTakeover"/>（权威）。
    /// </summary>
    private string? _pauseReason;
    private string? _takeoverReason;

    public DesktopInteractionState(BrowserAutomationAuthority authority)
    {
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
    }

    /// <summary>背后的事实源；诊断与投影用。</summary>
    public BrowserAutomationAuthority Authority => _authority;

    /// <summary>暂停轴：读的是权威快照，不是本类自己的标志。</summary>
    public bool IsPaused => _authority.Capture().IsPaused;

    /// <summary>接管轴：读的是权威快照。</summary>
    public bool IsUserTakeover => _authority.Capture().IsUserTakeover;

    /// <summary>当前保持（接管或暂停）的原因；由权威记录。</summary>
    public string? Reason => _authority.Capture().Reason;

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

    /// <summary>工具运行时被暂停（用户或准入策略）；写入权威。</summary>
    public void Pause(string reason)
    {
        _pauseReason = Normalize(reason);
        _authority.SetPaused(true, _pauseReason);
    }

    /// <summary>解除暂停（只动暂停轴，不影响接管）；仍保持接管时把接管原因留在权威里。</summary>
    public void Resume()
    {
        _pauseReason = null;
        _authority.SetPaused(false, _takeoverReason);
    }

    /// <summary>用户接管了浏览器：自动化必须立即停止（已发出的 UI 调用由 deadline/取消收尾）。</summary>
    public void NotifyUserTakeover(string reason)
    {
        _takeoverReason = Normalize(reason);
        _authority.SetUserTakeover(true, _takeoverReason);
    }

    /// <summary>解除接管（阅读授权不因此撤销）；仍在暂停时把暂停原因留在权威里。</summary>
    public void ClearUserTakeover()
    {
        _takeoverReason = null;
        _authority.SetUserTakeover(false, _pauseReason);
    }

    /// <summary>
    /// 布尔形态的接管写入（供执行闸门端口调用）。<c>true</c> 等价于
    /// <see cref="NotifyUserTakeover"/>，<c>false</c> 等价于 <see cref="ClearUserTakeover"/>。
    /// </summary>
    public void SetUserTakeover(bool takenOver)
    {
        if (takenOver)
        {
            NotifyUserTakeover("takeover");
            return;
        }

        ClearUserTakeover();
    }

    /// <summary>布尔形态的暂停写入（供执行闸门端口调用）。</summary>
    public void SetPaused(bool paused)
    {
        if (paused)
        {
            Pause("paused");
            return;
        }

        Resume();
    }

    private static string Normalize(string? reason) =>
        string.IsNullOrWhiteSpace(reason) ? "unspecified" : reason.Trim();
}
