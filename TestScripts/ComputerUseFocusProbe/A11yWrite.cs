using System.Diagnostics;
using System.Windows.Automation;

namespace ComputerUseFocusProbe;

/// <summary>
/// 在目标区域里找第一个可写的编辑节点并尝试写值。
/// 分层报告：找到没有 / 有但无 ValuePattern / 有但只读 / 写入后读回是否一致。
/// 只有「读回一致」才算写成功，SetValue 本身不抛异常不算证据。
/// </summary>
internal static class A11yWrite
{
    public static int Run(nint hwnd, int left, int top, int right, int bottom, string text)
    {
        var root = AutomationElement.FromHandle(hwnd);
        if (root is null)
        {
            Console.WriteLine("UIA_ROOT=none");
            return 1;
        }

        var sw = Stopwatch.StartNew();
        var edit = Find(root, TreeWalker.RawViewWalker, 0, sw, left, top, right, bottom);
        if (edit is null)
        {
            Console.WriteLine($"NO_EDIT_ELSEWHERE ms={sw.ElapsedMilliseconds}");
            return 2;
        }

        var r = edit.Current.BoundingRectangle;
        var name = edit.Current.Name ?? string.Empty;
        Console.WriteLine($"EDIT cls={edit.Current.ClassName} name_len={name.Length} rect={(int)r.X},{(int)r.Y} {(int)r.Width}x{(int)r.Height} enabled={edit.Current.IsEnabled}");

        var hasValue = edit.TryGetCurrentPattern(ValuePattern.Pattern, out var vpObj);
        Console.WriteLine($"PATTERNS value={hasValue}");

        if (!hasValue)
        {
            Console.WriteLine("NO_VALUEPATTERN");
            return 3;
        }

        var vp = (ValuePattern)vpObj;
        Console.WriteLine($"READONLY={vp.Current.IsReadOnly} BEFORE_LEN={vp.Current.Value.Length}");

        if (vp.Current.IsReadOnly)
        {
            Console.WriteLine("READONLY_REJECTED");
            return 4;
        }

        vp.SetValue(text);
        Thread.Sleep(400);
        var after = vp.Current.Value;
        Console.WriteLine($"AFTER_LEN={after.Length} MATCH={string.Equals(after, text, StringComparison.Ordinal)}");
        return string.Equals(after, text, StringComparison.Ordinal) ? 0 : 5;
    }

    private static AutomationElement? Find(AutomationElement el, TreeWalker walker, int depth, Stopwatch sw,
        int left, int top, int right, int bottom)
    {
        if (sw.Elapsed > TimeSpan.FromSeconds(10) || depth > 14)
        {
            return null;
        }

        try
        {
            if (el.Current.ControlType == ControlType.Edit)
            {
                var rr = el.Current.BoundingRectangle;
                if (!rr.IsEmpty && rr.Right > left && rr.Left < right && rr.Bottom > top && rr.Top < bottom)
                {
                    return el;
                }
            }
        }
        catch
        {
            // 属性读不到就继续找。
        }

        try
        {
            var c = walker.GetFirstChild(el);
            while (c is not null)
            {
                var hit = Find(c, walker, depth + 1, sw, left, top, right, bottom);
                if (hit is not null)
                {
                    return hit;
                }

                c = walker.GetNextSibling(c);
            }
        }
        catch
        {
            // 序列中断，返回已找到的结果。
        }

        return null;
    }
}
