using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PuddingChat.WinUI;

namespace PuddingChat.WinUITests;

public partial class App
{
    private sealed class InspectionClient : ISubAgentInspectionClient
    {
        public int Calls;
        public Func<SubAgentInspectionKey, Task<SubAgentInspection>> Handler = _ => throw new IOException();
        public Task<SubAgentInspection> ReadAsync(SubAgentInspectionKey key, CancellationToken ct) { Calls++; return Handler(key); }
    }
    private static async Task VerifySubAgentInspectorAsync(Grid root)
    {
        var key = new SubAgentInspectionKey(new("workspace", "parent"), "parent-session", "run-1");
        var snapshot = new SubAgentInspection(key, "completed", "核对实现", new string('结', 1000),
            [new("thought", "thinking", "done", "归档思考", 1), new("call", "tool_call", "running", "检查", 2, "read", ToolCallId: "call")],
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(1), 2, 1, ArchiveWarning: "归档缺失 1 条事件");
        var client = new InspectionClient();
        using var view = new SubAgentInspector(key, client) { Height = 500, Width = 650 };
        Grid.SetColumnSpan(view, 2); root.Children.Add(view);
        try
        {
            await UntilAsync(() => view.IsLoaded);
            var pending = new TaskCompletionSource<SubAgentInspection>(); client.Handler = _ => pending.Task;
            var first = view.LoadAsync(); var repeated = view.LoadAsync();
            Check(ReferenceEquals(first, repeated) && client.Calls == 1, "inspector deduplicates concurrent archive reads");
            pending.SetResult(snapshot); await first; root.UpdateLayout();
            var panel = (StackPanel)((ScrollViewer)view.Content).Content;
            var sections = panel.Children.OfType<Expander>().ToArray();
            Check(view.Snapshot == snapshot && sections.Length == 3 && ((MarkdownView)sections[2].Content).Children.OfType<TextBlock>().Single().Text.Length == 1000,
                "native inspector separates task, execution and untruncated result");
            Check(panel.Children.OfType<InfoBar>().Any(b => b.IsOpen && b.Message.Contains("缺失")),
                "archive degradation is visible instead of presenting complete history");
            client.Handler = _ => throw new IOException("fixture-only failure"); await view.LoadAsync();
            Check(view.Snapshot == snapshot && view.LoadError.Contains("上次"), "failed refresh preserves snapshot with stale notice");
            client.Handler = _ => Task.FromResult(snapshot with { Key = key with { RunId = "run-2" } }); await view.LoadAsync();
            Check(view.Snapshot == snapshot && view.LoadError.Length > 0, "different child invocation cannot replace inspected run");
            client.Handler = _ => Task.FromResult(snapshot with { Key = key with { Role = new("workspace", "other") } }); await view.LoadAsync();
            Check(view.Snapshot == snapshot && view.LoadError.Length > 0, "cross-role archive response is rejected");
            var late = new TaskCompletionSource<SubAgentInspection>(); client.Handler = _ => late.Task;
            var closing = view.LoadAsync(); view.Dispose(); await closing;
            late.SetResult(snapshot with { Status = "failed" }); await view.LoadAsync();
            Check(view.Snapshot == snapshot && client.Calls == 5, "disposed inspector cancels wait and ignores late archive results");
        }
        finally { root.Children.Remove(view); }
    }
}
