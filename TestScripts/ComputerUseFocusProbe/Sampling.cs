using System.Diagnostics;
using FlaUI.UIA3;
using Interop.UIAutomationClient;
using WpfAutomationElement = System.Windows.Automation.AutomationElement;
using WpfTreeWalker = System.Windows.Automation.TreeWalker;
using ComTreeWalker = Interop.UIAutomationClient.IUIAutomationTreeWalker;
using ComElement = Interop.UIAutomationClient.IUIAutomationElement;

namespace ComputerUseFocusProbe;

/// <summary>
/// 稳定性采样：同一进程内轮流用三种 UIA 客户端枚举同一个窗口，多轮重复。
///
/// 为什么需要它：实测同一个客户端在不同时刻给出 606 / 78 / 4 个节点，
/// 跨进程、跨时刻的单次对比根本无法区分「库不行」与「这一刻树没建好」。
/// 只有同一进程内交错重复采样，才能把这两个变量分开。
///
/// 每轮每个客户端都记录：时间戳、锚点（根节点类名是否读得到）、节点数、耗时。
/// 结束时按客户端给 min/max/唯一值集合，稳定才算可用的判据。
/// </summary>
internal static class Sampling
{
    private sealed record Sample(int Round, string Client, string Anchor, int Nodes, long Ms);

    public static int Run(nint hwnd, int rounds, int intervalMs, string firstClient)
    {
        Console.WriteLine($"SAMPLE hwnd=0x{(long)hwnd:X} rounds={rounds} interval={intervalMs}ms first={firstClient}");
        var samples = new List<Sample>(rounds * 3);

        var clients = new List<(string Name, Func<(string, int)> Probe)>
        {
            ("wpf-uia", () => CountWpf(hwnd)),
            ("flaui-uia3", () => CountFlaUi(hwnd)),
            ("raw-com", () => CountCom(hwnd)),
        };

        var idx = clients.FindIndex(c => string.Equals(c.Name, firstClient, StringComparison.OrdinalIgnoreCase));
        if (idx > 0)
        {
            var first = clients[idx];
            clients.RemoveAt(idx);
            clients.Insert(0, first);
        }

        for (var round = 1; round <= rounds; round++)
        {
            foreach (var client in clients)
            {
                samples.Add(Measure(round, client.Name, client.Probe));
            }

            if (round < rounds && intervalMs > 0)
            {
                Thread.Sleep(intervalMs);
            }
        }

        Console.WriteLine();
        Console.WriteLine("round | client     | anchor              | nodes | ms");
        Console.WriteLine("------+------------+---------------------+-------+-----");
        foreach (var s in samples)
        {
            Console.WriteLine($"{s.Round,5} | {s.Client,-10} | {s.Anchor,-19} | {s.Nodes,5} | {s.Ms}");
        }

        Console.WriteLine();
        Console.WriteLine("client     | min | max | distinct");
        Console.WriteLine("-----------+-----+-----+---------");
        foreach (var group in samples.GroupBy(s => s.Client))
        {
            var values = group.Select(s => s.Nodes).ToList();
            var distinct = string.Join(",", values.Distinct().OrderBy(v => v));
            Console.WriteLine($"{group.Key,-10} | {values.Min(),3} | {values.Max(),3} | {distinct}");
        }

        return 0;
    }

    private static Sample Measure(int round, string client, Func<(string Anchor, int Nodes)> probe)
    {
        var clock = Stopwatch.StartNew();
        var (anchor, nodes) = probe();
        clock.Stop();
        return new Sample(round, client, anchor, nodes, clock.ElapsedMilliseconds);
    }

    private static (string, int) CountWpf(nint hwnd)
    {
        try
        {
            var root = WpfAutomationElement.FromHandle(hwnd);
            if (root is null)
            {
                return ("null-root", -1);
            }

            var anchor = root.Current.ClassName ?? "?";
            var clock = Stopwatch.StartNew();
            return (anchor, WalkWpf(WpfTreeWalker.RawViewWalker, root, 0, clock));
        }
        catch (Exception ex)
        {
            return ("fail:" + ex.GetType().Name, -1);
        }
    }

    private static int WalkWpf(WpfTreeWalker walker, WpfAutomationElement element, int depth, Stopwatch clock)
    {
        if (depth > 14 || clock.Elapsed > TimeSpan.FromSeconds(8))
        {
            return 0;
        }

        var n = 1;
        try
        {
            var child = walker.GetFirstChild(element);
            while (child is not null)
            {
                n += WalkWpf(walker, child, depth + 1, clock);
                child = walker.GetNextSibling(child);
            }
        }
        catch
        {
            // 子树中断：已计数部分仍有效。
        }

        return n;
    }

    private static (string, int) CountFlaUi(nint hwnd)
    {
        try
        {
            using var automation = new UIA3Automation();
            var root = automation.FromHandle(hwnd);
            if (root is null)
            {
                return ("null-root", -1);
            }

            var anchor = root.ClassName ?? "?";
            var clock = Stopwatch.StartNew();
            return (anchor, WalkFlaUi(root, 0, clock));
        }
        catch (Exception ex)
        {
            return ("fail:" + ex.GetType().Name, -1);
        }
    }

    private static int WalkFlaUi(FlaUI.Core.AutomationElements.AutomationElement element, int depth, Stopwatch clock)
    {
        if (depth > 14 || clock.Elapsed > TimeSpan.FromSeconds(8))
        {
            return 0;
        }

        var n = 1;
        try
        {
            foreach (var child in element.FindAllChildren())
            {
                n += WalkFlaUi(child, depth + 1, clock);
            }
        }
        catch
        {
            // 子树中断。
        }

        return n;
    }

    private static (string, int) CountCom(nint hwnd)
    {
        try
        {
            var uia = (IUIAutomation)new CUIAutomation8();
            var root = uia.ElementFromHandle(hwnd);
            if (root is null)
            {
                return ("null-root", -1);
            }

            var anchor = root.CurrentClassName ?? "?";
            var clock = Stopwatch.StartNew();
            return (anchor, WalkCom(uia.RawViewWalker, root, 0, clock));
        }
        catch (Exception ex)
        {
            return ("fail:" + ex.GetType().Name, -1);
        }
    }

    private static int WalkCom(ComTreeWalker walker, ComElement element, int depth, Stopwatch clock)
    {
        if (depth > 14 || clock.Elapsed > TimeSpan.FromSeconds(8))
        {
            return 0;
        }

        var n = 1;
        try
        {
            var child = walker.GetFirstChildElement(element);
            while (child is not null)
            {
                n += WalkCom(walker, child, depth + 1, clock);
                child = walker.GetNextSiblingElement(child);
            }
        }
        catch
        {
            // 子树中断。
        }

        return n;
    }
}
