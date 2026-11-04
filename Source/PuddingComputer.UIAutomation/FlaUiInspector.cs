using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA2;
using FlaUI.UIA3;
using Pudding.Contracts.Computer;

namespace PuddingComputer.UIAutomation;

/// <summary>
/// 基于 FlaUI.UIA3 的只读观察器。
/// <para>
/// 广度优先遍历，因为契约要求「父在子前」，而 BFS 天然满足这一点；
/// DFS 也能做到，但要在每个节点上先追加父再递归，写起来更容易出错。
/// </para>
/// <para>
/// 所有遍历都受 <see cref="UiaCaptureOptions"/> 限制：深度、节点数、总时长。
/// 任何一个超限都返回明确状态，**不静默截断**：静默截断会让上层以为
/// "这个应用只有这么多元素"，而实际上只是我们没读完。
/// </para>
/// </summary>
public sealed class FlaUiInspector : IUiaInspector, IDisposable
{
    private readonly UIA3Automation _automation;
    private readonly bool _ownsAutomation;

    /// <summary>用自建 automation 实例（通常用法）。</summary>
    public FlaUiInspector()
    {
        _automation = new UIA3Automation();
        _ownsAutomation = true;
    }

    /// <summary>复用外部 automation 实例（多个 inspector 共享一个 COM 会话）。</summary>
    public FlaUiInspector(UIA3Automation automation)
    {
        ArgumentNullException.ThrowIfNull(automation);
        _automation = automation;
        _ownsAutomation = false;
    }

    /// <inheritdoc />
    public UiaSnapshot Capture(IntPtr windowHandle, UiaCaptureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var problems = options.Validate();
        if (problems.Count > 0)
            return UiaSnapshot.Empty(UiaAvailability.Unavailable,
                $"参数非法：{string.Join("; ", problems)}");

        if (windowHandle == IntPtr.Zero)
            return UiaSnapshot.Empty(UiaAvailability.Unavailable, "窗口句柄为空");

        var clock = Stopwatch.StartNew();
        var nodes = new List<UiaNode>(capacity: 256);

        // BFS 队列：(元素, 深度, 父引用)
        var queue = new Queue<(AutomationElement Element, int Depth, string? ParentRef)>();
        string? rootRef;

        try
        {
            // 先唤醒再取根：Chromium 系的树不会被 UIA3 客户端唤醒，而在唤醒前拿到的 UIA3 根元素
            // 会缓存一个空子树，导致本次采集白白失败。UIA2 客户端探测同时承担唤醒职责。
            // 机制说明仅为当前工作假设：FlaUI.UIA2 实际引用的是托管 UIAutomationClient（见 2026-11-04 核实），
            // 因此不能把差异归因于“MSAA 与 UIA”之别，只能归因于“UIA2 客户端先探一次”这个动作顺序。
            _ = ProbeChildCount(windowHandle);

            var root = _automation.FromHandle(windowHandle);
            if (root is null)
                return UiaSnapshot.Empty(UiaAvailability.Unavailable, "FromHandle 返回 null（句柄无效或窗口已关闭）");

            rootRef = "n0";
            nodes.Add(Normalize(root, rootRef, parentRef: null));

            // 预热：Chromium 系窗口的 UIA 树是增量构建的，第一次探测往往只能看到 0~1 个子节点。
            // 窗口有界，且一旦读到子节点立即停止；读不到就照常采集，不把等待当成成功。
            var warmupWindow = UiaStabilization.EffectiveWarmup(options.EffectiveTimeout, UiaStabilization.DefaultWarmupWindow);
            var warmupClock = Stopwatch.StartNew();
            var childrenSeen = ProbeChildCount(windowHandle);
            while (UiaStabilization.ShouldKeepWarming(childrenSeen, warmupClock.Elapsed, warmupWindow))
            {
                Thread.Sleep(UiaStabilization.DefaultPollInterval);
                childrenSeen = ProbeChildCount(windowHandle);
            }

            queue.Enqueue((root, 0, rootRef));
        }
        catch (Exception ex)
        {
            return UiaSnapshot.Empty(UiaAvailability.Unavailable,
                $"附着窗口失败：{ex.GetType().Name}: {ex.Message}");
        }

        bool timedOut = false;
        string? error = null;

        while (queue.Count > 0)
        {
            if (clock.Elapsed > options.EffectiveTimeout)
            {
                timedOut = true;
                break;
            }

            if (nodes.Count >= options.MaxNodes)
                break;

            var (element, depth, parentRef) = queue.Dequeue();

            if (depth >= options.MaxDepth)
                continue;

            AutomationElement[] children;
            try
            {
                children = element.FindAllChildren();
            }
            catch
            {
                // 单个节点的子枚举失败不该让整次观察失败 —— 继续其它分支。
                continue;
            }

            foreach (var child in children)
            {
                if (nodes.Count >= options.MaxNodes) break;
                if (clock.Elapsed > options.EffectiveTimeout) { timedOut = true; break; }

                string childRef = $"n{nodes.Count}";
                try
                {
                    nodes.Add(Normalize(child, childRef, parentRef));
                    queue.Enqueue((child, depth + 1, childRef));
                }
                catch (Exception ex)
                {
                    // 归一化单个节点失败：跳过它，记录首个错误便于诊断。
                    error ??= $"节点归一化失败：{ex.GetType().Name}";
                }
            }

            if (timedOut) break;
        }

        var availability = UiaAvailabilityClassifier.Classify(
            nodes.Count, nodes.Count(n => n.IsActionable), timedOut, error);

        string? reason = availability switch
        {
            UiaAvailability.TimedOut =>
                $"枚举超时（{options.EffectiveTimeout.TotalMilliseconds:F0} ms），已读 {nodes.Count} 个节点；这不代表该应用不支持无障碍",
            UiaAvailability.Unavailable => error ?? "未读到任何节点",
            UiaAvailability.Sparse =>
                $"只读到 {nodes.Count(n => n.IsActionable)} 个可操作节点（阈值 {UiaAvailabilityClassifier.SparseThreshold}），需要视觉兜底",
            _ => null,
        };

        return new UiaSnapshot(availability, nodes, reason);
    }

    /// <summary>
    /// 用 UIA2 客户端（FlaUI.UIA2）探测根节点的子节点数。
    /// <para>
    /// 为什么不用 UIA3 自己探：实测（2026-11-04）Chromium 系窗口的无障碍树**不会被 UIA3 客户端唤醒**——
    /// 同一进程内 uia3 首次只读到 1 个节点，而先跑一次 uia2 后 uia3 立刻能读到子节点。
    /// 因此这里既在“看”也在“唤醒”，双重职责是有意的。
    /// </para>
    /// <para>
    /// 命名保留为 UIA2 而非 MSAA：2026-11-04 核实 FlaUI.UIA2.dll 的 AssemblyRef 指向
    /// UIAutomationClient / System.Windows.Automation（托管 UIA 客户端），不是 IAccessible。
    /// 所以“UIA2 有效、UIA3 无效”是**动作序列**层面的观测，底层机制尚未被单独证明。
    /// </para>
    /// </summary>
    private static int ProbeChildCount(IntPtr windowHandle)
    {
        try
        {
            using var automation = new UIA2Automation();
            var root = automation.FromHandle(windowHandle);
            return root?.FindAllChildren().Length ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 把一个 UIA 元素归一化为契约记录。
    /// <para>
    /// 每个属性单独 try：UIA 在元素被销毁、跨进程失效、或属性本身不支持时会抛，
    /// 一个属性抛不该让整条记录丢失。
    /// </para>
    /// </summary>
    private static UiaNode Normalize(AutomationElement element, string reference, string? parentRef)
    {
        string? automationId = null;
        try { automationId = element.Properties.AutomationId.ValueOrDefault; } catch { }

        string name = "";
        try { name = element.Properties.Name.ValueOrDefault ?? ""; } catch { }

        string controlType = "";
        try { controlType = element.ControlType.ToString(); } catch { }

        string className = "";
        try { className = element.ClassName ?? ""; } catch { }

        var bounds = default(PhysicalRect);
        try
        {
            var r = element.Properties.BoundingRectangle.ValueOrDefault;
            if (!r.IsEmpty)
                bounds = new PhysicalRect(r.X, r.Y, r.Width, r.Height);
        }
        catch { }

        bool enabled = true;
        try { enabled = element.Properties.IsEnabled.ValueOrDefault; } catch { }

        bool offscreen = false;
        try { offscreen = element.Properties.IsOffscreen.ValueOrDefault; } catch { }

        bool focused = false;
        try { focused = element.Properties.HasKeyboardFocus.ValueOrDefault; } catch { }

        var patterns = new List<string>(4);
        try { if (element.Patterns.Invoke.IsSupported) patterns.Add("Invoke"); } catch { }
        try { if (element.Patterns.Value.IsSupported) patterns.Add("Value"); } catch { }
        try { if (element.Patterns.SelectionItem.IsSupported) patterns.Add("SelectionItem"); } catch { }
        try { if (element.Patterns.Toggle.IsSupported) patterns.Add("Toggle"); } catch { }
        try { if (element.Patterns.Text.IsSupported) patterns.Add("Text"); } catch { }

        return new UiaNode(reference, automationId, name, controlType, className,
            bounds, enabled, offscreen, focused, patterns, parentRef);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsAutomation)
            _automation.Dispose();
    }
}
