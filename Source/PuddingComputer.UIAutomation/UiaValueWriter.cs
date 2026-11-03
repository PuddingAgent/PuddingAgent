using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using Pudding.Contracts.Computer;

namespace PuddingComputer.UIAutomation;

/// <summary>写值结果。区分「没找到 / 有歧义 / 不可写 / 只读 / 写了但读回不符 / 读回一致」。</summary>
public enum UiaWriteStatus
{
    /// <summary>选择器没有命中任何元素。</summary>
    NotFound,

    /// <summary>命中多个元素。不替调用方挑第一个 —— 那是最容易写错地方的做法。</summary>
    Ambiguous,

    /// <summary>元素存在，但不支持 ValuePattern。</summary>
    NotWritable,

    /// <summary>支持 ValuePattern 但标记为只读。</summary>
    ReadOnly,

    /// <summary>已调用 SetValue，但重新解析后读回值与期望不符。</summary>
    ReadBackMismatch,

    /// <summary>重新解析后读回值与期望完全一致。</summary>
    Verified,

    /// <summary>UIA 调用抛错（provider 阻塞、元素失效等）。</summary>
    Failed,
}

/// <summary>写值结果。只有 <see cref="Verified"/> 才算成功。</summary>
public sealed record UiaWriteResult(UiaWriteStatus Status, string? ElementName, string? ReadBack, string? Detail)
{
    public bool IsVerified => Status == UiaWriteStatus.Verified;
}

/// <summary>调用结果。调用成功不等于产生了预期效果，效果必须由调用方另行验证。</summary>
public sealed record UiaInvokeResult(UiaInvokeStatus Status, string? ElementName, string? Detail)
{
    public bool Invoked => Status == UiaInvokeStatus.Invoked;
}

/// <summary>Invoke 结果。</summary>
public enum UiaInvokeStatus
{
    NotFound,
    Ambiguous,
    NotInvocable,
    Invoked,
    Failed,
}

/// <summary>
/// 通用的无障碍写值与调用。<b>不含任何针对特定应用的逻辑。</b>
/// <para>
/// 为什么需要它：很多桌面应用（尤其 Chromium 系）对合成键盘输入无反应，
/// 但通过 UI Automation 的 ValuePattern 仍能写入文本。这条路绕开的是输入注入，
/// 不是绕开权限 —— 目标进程自己决定是否暴露可写节点。
/// </para>
/// <para>
/// 判定纪律：
/// ① 空选择器不匹配任何元素（与 <see cref="UiaNodeMatching"/> 保持一致）；
/// ② 命中多个一律报歧义，不挑第一个；
/// ③ 写完**必须重新解析并读回**，读回一致才算成功 —— SetValue 不抛异常不是证据；
/// ④ provider 可能单次阻塞，因此整体有节点/深度/时长预算，超限如实报失败。
/// </para>
/// </summary>
public sealed class UiaValueWriter : IDisposable
{
    private const int MaxDepth = 14;
    private const int MaxNodes = 2000;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(6);

    private readonly UIA3Automation _automation;
    private readonly bool _ownsAutomation;

    public UiaValueWriter()
    {
        _automation = new UIA3Automation();
        _ownsAutomation = true;
    }

    public UiaValueWriter(UIA3Automation automation)
    {
        ArgumentNullException.ThrowIfNull(automation);
        _automation = automation;
        _ownsAutomation = false;
    }

    /// <summary>按选择器写入文本，并在写入后重新解析、读回比对。</summary>
    public UiaWriteResult WriteValue(IntPtr windowHandle, ElementSelector selector, string text)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(text);

        if (!selector.HasAny)
            return new UiaWriteResult(UiaWriteStatus.NotFound, null, null, "选择器为空，按规则不匹配任何元素");
        if (windowHandle == IntPtr.Zero)
            return new UiaWriteResult(UiaWriteStatus.Failed, null, null, "窗口句柄为空");

        try
        {
            var first = Resolve(windowHandle, selector);
            if (first.Status != ResolveStatus.Ok)
                return new UiaWriteResult(MapResolve(first.Status), null, null, first.Detail);

            var element = first.Element!;
            var name = SafeName(element);

            if (!element.Patterns.Value.IsSupported)
                return new UiaWriteResult(UiaWriteStatus.NotWritable, name, null, "元素不支持 ValuePattern");

            var pattern = element.Patterns.Value.Pattern;
            if (pattern.IsReadOnly)
                return new UiaWriteResult(UiaWriteStatus.ReadOnly, name, null, "元素标记为只读");

            pattern.SetValue(text);

            // 关键：重新解析，不复用写之前的元素引用 —— 它可能已经失效或指向旧节点。
            var again = Resolve(windowHandle, selector);
            if (again.Status != ResolveStatus.Ok)
                return new UiaWriteResult(UiaWriteStatus.Failed, name, null, "写入后重新解析失败：" + again.Detail);

            var readBack = again.Element!.Patterns.Value.IsSupported
                ? again.Element.Patterns.Value.Pattern.Value
                : null;

            return string.Equals(readBack, text, StringComparison.Ordinal)
                ? new UiaWriteResult(UiaWriteStatus.Verified, name, readBack, null)
                : new UiaWriteResult(UiaWriteStatus.ReadBackMismatch, name, readBack, "读回值与写入值不一致");
        }
        catch (Exception ex)
        {
            return new UiaWriteResult(UiaWriteStatus.Failed, null, null, ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>按选择器调用元素（InvokePattern）。</summary>
    public UiaInvokeResult Invoke(IntPtr windowHandle, ElementSelector selector)
    {
        ArgumentNullException.ThrowIfNull(selector);

        if (!selector.HasAny)
            return new UiaInvokeResult(UiaInvokeStatus.NotFound, null, "选择器为空，按规则不匹配任何元素");
        if (windowHandle == IntPtr.Zero)
            return new UiaInvokeResult(UiaInvokeStatus.Failed, null, "窗口句柄为空");

        try
        {
            var resolved = Resolve(windowHandle, selector);
            if (resolved.Status != ResolveStatus.Ok)
                return new UiaInvokeResult(MapInvoke(resolved.Status), null, resolved.Detail);

            var element = resolved.Element!;
            var name = SafeName(element);
            if (!element.Patterns.Invoke.IsSupported)
                return new UiaInvokeResult(UiaInvokeStatus.NotInvocable, name, "元素不支持 InvokePattern");

            element.Patterns.Invoke.Pattern.Invoke();
            return new UiaInvokeResult(UiaInvokeStatus.Invoked, name,
                "已调用 Invoke；是否产生预期效果需调用方另行验证");
        }
        catch (Exception ex)
        {
            return new UiaInvokeResult(UiaInvokeStatus.Failed, null, ex.GetType().Name + ": " + ex.Message);
        }
    }

    private enum ResolveStatus { Ok, NotFound, Ambiguous, Failed }

    private readonly record struct ResolveResult(ResolveStatus Status, AutomationElement? Element, string? Detail);

    private ResolveResult Resolve(IntPtr windowHandle, ElementSelector selector)
    {
        var clock = Stopwatch.StartNew();
        var root = _automation.FromHandle(windowHandle);
        if (root is null)
            return new ResolveResult(ResolveStatus.Failed, null, "FromHandle 返回 null（句柄无效或窗口已关闭）");

        var hits = new List<AutomationElement>(capacity: 2);
        var visited = 0;
        var stack = new Stack<(AutomationElement Element, int Depth)>();
        stack.Push((root, 0));

        while (stack.Count > 0)
        {
            if (++visited > MaxNodes || clock.Elapsed > Budget)
                return new ResolveResult(ResolveStatus.Failed, null, $"遍历超出预算（节点 {visited}，用时 {clock.ElapsedMilliseconds} ms）");

            var (element, depth) = stack.Pop();
            try
            {
                if (Matches(element, selector))
                {
                    hits.Add(element);
                    if (hits.Count > 1)
                        return new ResolveResult(ResolveStatus.Ambiguous, null, $"命中 {hits.Count} 个以上元素，拒绝任选其一");
                }
            }
            catch
            {
                // 单个元素的属性读不到就跳过，不影响整体解析。
            }

            if (depth >= MaxDepth)
                continue;

            try
            {
                foreach (var child in element.FindAllChildren())
                    stack.Push((child, depth + 1));
            }
            catch
            {
                // 子树中断：已收集的命中仍然有效。
            }
        }

        return hits.Count switch
        {
            0 => new ResolveResult(ResolveStatus.NotFound, null, "未命中任何元素"),
            _ => new ResolveResult(ResolveStatus.Ok, hits[0], null),
        };
    }

    /// <summary>
    /// 与 <see cref="UiaNodeMatching.Matches(UiaNode, ElementSelector)"/> 相同的比较规则：
    /// AutomationId / Name 精确比较，ControlType / ClassName 忽略大小写；空选择器不匹配。
    /// </summary>
    private static bool Matches(AutomationElement element, ElementSelector selector)
        => UiaNodeMatching.MatchesFields(
            selector,
            element.AutomationId,
            element.Name,
            element.ControlType.ToString(),
            element.ClassName);

    private static string? SafeName(AutomationElement element)
    {
        try
        {
            return element.Name;
        }
        catch
        {
            return null;
        }
    }

    private static UiaWriteStatus MapResolve(ResolveStatus status) => status switch
    {
        ResolveStatus.NotFound => UiaWriteStatus.NotFound,
        ResolveStatus.Ambiguous => UiaWriteStatus.Ambiguous,
        _ => UiaWriteStatus.Failed,
    };

    private static UiaInvokeStatus MapInvoke(ResolveStatus status) => status switch
    {
        ResolveStatus.NotFound => UiaInvokeStatus.NotFound,
        ResolveStatus.Ambiguous => UiaInvokeStatus.Ambiguous,
        _ => UiaInvokeStatus.Failed,
    };

    public void Dispose()
    {
        if (_ownsAutomation)
            _automation.Dispose();
    }
}
