using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using POINT = ComputerUseFocusProbe.Native.POINT;

namespace ComputerUseFocusProbe;

/// <summary>
/// 无障碍能力探测：回答「这个进程到底有没有可用的无障碍树，能不能直接写值」。
///
/// 判据是分层的，任何一层拿不到都单独报，不合并成「支持 / 不支持」：
///   1. UIA Control View（应用通常只暴露这一层，可能为空）
///   2. UIA Raw View（能看到多少原始节点）
///   3. MSAA：AccessibleObjectFromWindow(OBJID_CLIENT) 是否返回对象
/// 全部有界：超过节点预算或时间预算就如实截断，不无限枚举。
/// </summary>
internal static class A11y
{
    private const int MaxNodes = 4000;
    private const int MaxDepth = 14;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(8);

    public static int Run(nint hwnd, int rectLeft, int rectTop, int rectRight, int rectBottom)
    {
        var sw = Stopwatch.StartNew();
        var root = AutomationElement.FromHandle(hwnd);
        if (root is null)
        {
            Console.WriteLine("UIA_ROOT=none");
            return 1;
        }

        Console.WriteLine($"UIA_ROOT class={root.Current.ClassName} name_len={(root.Current.Name ?? string.Empty).Length}");

        var controlCount = Count(root, TreeWalker.ControlViewWalker, sw, out var controlTruncated);
        Console.WriteLine($"UIA_CONTROL_VIEW nodes={controlCount} truncated={controlTruncated} ms={sw.ElapsedMilliseconds}");

        sw.Restart();
        var rawNodes = new List<string>();
        var rawCount = Walk(root, TreeWalker.RawViewWalker, 0, sw, rawNodes, rectLeft, rectTop, rectRight, rectBottom, out var rawTruncated);
        Console.WriteLine($"UIA_RAW_VIEW nodes={rawCount} truncated={rawTruncated} ms={sw.ElapsedMilliseconds}");

        Console.WriteLine($"REGION ({rectLeft},{rectTop})-({rectRight},{rectBottom}) hits={rawNodes.Count}");
        foreach (var line in rawNodes.Take(40))
        {
            Console.WriteLine("  " + line);
        }

        ProbeMsaa("root", hwnd);
        var child = Native.WindowFromPoint(new POINT { X = (rectLeft + rectRight) / 2, Y = (rectTop + rectBottom) / 2 });
        if (child != nint.Zero && child != hwnd)
        {
            ProbeMsaa("point_child", child);
        }

        return 0;
    }

    private static int Count(AutomationElement root, TreeWalker walker, Stopwatch sw, out bool truncated)
    {
        truncated = false;
        var n = 0;
        var stack = new Stack<(AutomationElement el, int depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            if (sw.Elapsed > Budget || n > MaxNodes)
            {
                truncated = true;
                break;
            }

            var (el, depth) = stack.Pop();
            n++;
            if (depth >= MaxDepth)
            {
                continue;
            }

            try
            {
                var c = walker.GetFirstChild(el);
                while (c is not null)
                {
                    stack.Push((c, depth + 1));
                    c = walker.GetNextSibling(c);
                }
            }
            catch
            {
                // 单个节点取不到子级不影响整体统计。
            }
        }

        return n;
    }

    private static int Walk(AutomationElement root, TreeWalker walker, int depth, Stopwatch sw,
        List<string> regionHits, int left, int top, int right, int bottom, out bool truncated)
    {
        truncated = false;
        if (sw.Elapsed > Budget)
        {
            truncated = true;
            return 0;
        }

        var n = 1;
        try
        {
            var r = root.Current.BoundingRectangle;
            if (!r.IsEmpty && r.Right > left && r.Left < right && r.Bottom > top && r.Top < bottom)
            {
                var name = root.Current.Name ?? string.Empty;
                regionHits.Add(
                    $"ct={root.Current.ControlType.ProgrammaticName} cls={root.Current.ClassName} " +
                    $"name_len={name.Length} rect={Fmt(r)} offscreen={root.Current.IsOffscreen}");
            }
        }
        catch
        {
            // 属性取不到就跳过这一条命中记录。
        }

        if (depth >= MaxDepth || n > MaxNodes)
        {
            truncated = depth >= MaxDepth;
            return n;
        }

        try
        {
            var c = walker.GetFirstChild(root);
            while (c is not null)
            {
                if (sw.Elapsed > Budget || n > MaxNodes)
                {
                    truncated = true;
                    break;
                }

                n += Walk(c, walker, depth + 1, sw, regionHits, left, top, right, bottom, out var t);
                truncated |= t;
                c = walker.GetNextSibling(c);
            }
        }
        catch
        {
            // 序列中断：已收集到的节点仍然有效，但标记截断。
            truncated = true;
        }

        return n;
    }

    private static string Fmt(System.Windows.Rect r)
        => $"{(int)r.X},{(int)r.Y} {(int)r.Width}x{(int)r.Height}";

    private static void ProbeMsaa(string label, nint hwnd)
    {
        var iid = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");
        var hr = AccessibleObjectFromWindow(hwnd, 0xFFFFFFFC /* OBJID_CLIENT */, ref iid, out var obj);
        var ok = hr == 0 && obj is not null;
        Console.WriteLine($"MSAA {label} hwnd=0x{hwnd:X} hr=0x{hr:X8} object={(ok ? obj!.GetType().FullName : "null")}");
    }

    [DllImport("oleacc.dll", PreserveSig = true)]
    private static extern int AccessibleObjectFromWindow(
        nint hwnd, uint dwId, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppvObject);
}
