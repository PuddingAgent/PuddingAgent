using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PuddingChat;
using PuddingChat.WinUI;

namespace PuddingChat.WinUITests;

public partial class App
{
    private async Task VerifyWorkspaceCancellationAsync(Grid root)
    {
        var reply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = new Fixture("") { Sent = PendingSend.Create(new("test", "builder"), "session", "work"),
            Cancellation = _ => reply.Task };
        using var view = new ChatWorkspace(fixture);
        Grid.SetColumnSpan(view, 2); root.Children.Add(view);
        Button Stop() => Descendants<Button>(view.Composer).Single(b => b.Content?.ToString() is "停止" or "请求停止中…");
        InfoBar Notice() => Descendants<InfoBar>(view).Single(b => b.Title is "已请求停止" or "停止请求未确认" or "fixture-marker");
        try
        {
            await view.InitializeAsync(); await view.SelectRoleAsync("test", fixture.Builder);
            var pending = view.CancelAsync(); await view.CancelAsync();
            Check(fixture.CancellationCalls == 1 && !Stop().IsEnabled && Stop().Content?.ToString() == "请求停止中…",
                "stop request disables repeated invocation while Core receipt is pending");
            await view.SelectRoleAsync("test", fixture.Reviewer);
            Check(Stop().IsEnabled && Stop().Content?.ToString() == "停止", "another role is not blocked by pending cancellation");
            await view.SelectRoleAsync("test", fixture.Builder);
            Check(!Stop().IsEnabled, "returning to the original turn retains pending cancellation");
            var notice = Descendants<InfoBar>(view).First(b => !Descendants<InfoBar>(view.Composer).Contains(b));
            notice.Title = "fixture-marker"; notice.Severity = InfoBarSeverity.Warning;
            reply.SetResult(); await pending;
            Check(Notice().Title == "fixture-marker" && Stop().IsEnabled, "late stop receipt cannot overwrite reselected role feedback");

            fixture.Cancellation = _ => Task.FromException(new InvalidOperationException("fixture failure"));
            await view.CancelAsync();
            Check(Notice().Title == "停止请求未确认" && Notice().Severity == InfoBarSeverity.Error && Stop().IsEnabled,
                "failed stop request shows retry feedback without claiming stopped execution");
            fixture.Cancellation = null;
            await view.CancelAsync();
            Check(Notice().Title == "已请求停止" && Notice().Severity == InfoBarSeverity.Informational
                && view.CurrentConversation?.ActiveRun is not null, "stop retry resets error severity and leaves execution authoritative");

            reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Cancellation = _ => reply.Task;
            pending = view.CancelAsync(); view.Dispose(); reply.SetResult(); await pending;
            Check(fixture.Disposed, "disposed workspace ignores delayed cancellation receipt");
        }
        finally { root.Children.Remove(view); }
    }
}
