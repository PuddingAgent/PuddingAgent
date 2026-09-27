using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PuddingChat.WinUI;

namespace PuddingChat.WinUITests;

public partial class App
{
    private static async Task VerifyFlowWindowAsync(Grid root)
    {
        static ProcessItem[] Events(int count) => Enumerable.Range(0, count).Select(i =>
            new ProcessItem($"f{i}", "tool_call", "running", $"调用 {i}", i, "terminal", ToolCallId: $"c{i}")).ToArray();
        var window = new FlowWindow(); var expansions = new Dictionary<string, bool>();
        var flow = new TurnContentView(expansions, window); flow.Update(Events(100), "");
        var scroll = new ScrollViewer { Content = flow, Height = 320, Width = 760, VerticalAlignment = VerticalAlignment.Top };
        Grid.SetColumnSpan(scroll, 2); root.Children.Add(scroll);
        try
        {
            scroll.UpdateLayout(); await Task.Delay(50);
            Check(flow.HiddenCount == 60 && flow.Children.Count == 41, "long turn initially creates only forty activity blocks and a reveal action");
            var anchor = (Expander)flow.Children[1];
            var top = anchor.TransformToVisual(scroll).TransformPoint(new()).Y;
            flow.RevealEarlier(); await Task.Delay(50);
            Check(flow.HiddenCount == 36 && flow.Children.Count == 65 && flow.Children.Contains(anchor), "reveal adds older blocks without recreating existing controls");
            Check(Math.Abs(anchor.TransformToVisual(scroll).TransformPoint(new()).Y - top) < 2,
                "revealing earlier activity preserves the visible block position");
            flow.Update(Events(120), "");
            Check(flow.HiddenCount == 36 && flow.Children.Contains(anchor), "stream append retains explicitly revealed activity range");
            var recreated = new TurnContentView(expansions, window); recreated.Update(Events(120), "");
            Check(recreated.HiddenCount == 36, "recycled view retains disclosure range in nonvisual state");
            while (flow.HiddenCount > 0) flow.RevealEarlier();
            Check(flow.Children.Count == 120 && flow.Children.All(c => c is Expander), "all earlier activity remains reachable and reveal action disappears at start");
        }
        finally { root.Children.Remove(scroll); }
    }
}
