using Microsoft.UI.Xaml.Controls;
using PuddingChat.WinUI;

namespace PuddingChat.WinUITests;

public partial class App
{
    private static async Task VerifyMessageDetailsAsync(Grid root)
    {
        var message = new ChatMessage("details", "old-run", "agent", "角色", DateTimeOffset.UnixEpoch,
            "正文", "done", []);
        var old = new TaskCompletionSource<ProcessDetails>();
        var current = new TaskCompletionSource<ProcessDetails>();
        var calls = 0;
        var state = new MessageViewState();
        using var card = new MessageCard(message, () => ++calls == 1 ? old.Task : current.Task, state: state);
        Grid.SetColumnSpan(card, 2); root.Children.Add(card);
        try
        {
            root.UpdateLayout(); await UntilAsync(() => card.IsLoaded);
            var first = card.LoadProcessDetailsAsync();
            var disclosure = ((StackPanel)((Border)card.Content).Child).Children.OfType<Expander>().Single();
            Check(disclosure.Header.ToString()!.Contains("正在加载")
                && ((StackPanel)disclosure.Content).Children.OfType<ProgressRing>().Single().IsActive,
                "details display progress while the application call is pending");
            await card.LoadProcessDetailsAsync();
            Check(calls == 1, "message details deduplicate concurrent loads");
            var fresh = message with { RunId = "new-run", ProcessItems = [new("fresh", "thinking", "done", "最新思考", 5)] };
            card.Update(fresh);
            await first;
            var second = card.LoadProcessDetailsAsync();
            Check(calls == 2, "new run cancels old wait and can immediately load details");
            old.SetResult(new("details", [new("stale", "thinking", "done", "旧执行", 1)]));
            current.SetResult(new("details", [new("fresh", "thinking", "running", "旧快照", 5),
                new("history", "thinking", "done", "历史思考", 2)]));
            await second;
            Check(disclosure.Header.ToString()!.Contains("2 项")
                && ((StackPanel)disclosure.Content).Children.OfType<TextBlock>().Any(t => t.Text.Contains("已合并"))
                && !((StackPanel)disclosure.Content).Children.OfType<ProgressRing>().Any(),
                "loaded details explain where records appear and remove progress");
            static string Text(MessageCard value) => string.Concat(((StackPanel)((Border)value.Content).Child)
                .Children.OfType<TurnContentView>().Single().Children.OfType<Expander>()
                .SelectMany(e => ((StackPanel)((ScrollViewer)e.Content!).Content).Children.OfType<MarkdownView>()
                    .SelectMany(m => m.Children.OfType<TextBlock>())).Select(t => t.Text));
            Check(Text(card) == "历史思考最新思考", "late old-run details cannot contaminate the new run");
            Check(!Text(card).Contains("旧快照"),
                "detail snapshot cannot overwrite newer canonical state");
            card.Dispose();
            using var recycled = new MessageCard(fresh, () => throw new InvalidOperationException("must use cache"), state: state);
            await recycled.LoadProcessDetailsAsync();
            Check(Text(recycled) == "历史思考最新思考", "recycled card restores only current-run details");

            var pending = new TaskCompletionSource<ProcessDetails>();
            var transientState = new MessageViewState();
            var closing = new MessageCard(message, () => pending.Task, state: transientState);
            var loading = closing.LoadProcessDetailsAsync(); closing.Dispose(); await loading;
            pending.SetResult(new("details", [new("stale", "thinking", "done", "late", 1)]));
            var reloads = 0;
            using var reopened = new MessageCard(message, () => { reloads++; return Task.FromResult(new ProcessDetails("details", [])); }, state: transientState);
            await reopened.LoadProcessDetailsAsync();
            Check(reloads == 1, "disposed card cannot cache late detail results");

            var attempts = 0;
            using var mismatched = new MessageCard(message, () => Task.FromResult(new ProcessDetails(++attempts == 1 ? "wrong" : "details", [])));
            await mismatched.LoadProcessDetailsAsync();
            var failed = ((StackPanel)((Border)mismatched.Content).Child).Children.OfType<Expander>().Single();
            var retry = ((StackPanel)failed.Content).Children.OfType<Button>().Single();
            Check(failed.Header.ToString()!.Contains("加载失败") && retry.Content?.ToString() == "重试加载",
                "failed detail load offers an explicit retry without requiring collapse");
            ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(retry)
                .GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
            await UntilAsync(() => attempts == 2);
            Check(attempts == 2, "wrong-message details are rejected and remain retryable");
        }
        finally { root.Children.Remove(card); }
    }
}
