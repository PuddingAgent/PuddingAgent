using PuddingChat.WinUI;
using Microsoft.UI.Xaml.Controls;

namespace PuddingChat.WinUITests;

public partial class App
{
    private sealed class ApprovalClient : IChatApprovals
    {
        public List<ApprovalSubmission> Calls = [];
        public Func<ApprovalSubmission, Task<ChatApproval>> Handler = _ => throw new InvalidOperationException("test failure");
        public Task<ChatApproval> DecideAsync(ApprovalSubmission submission, CancellationToken ct) { Calls.Add(submission); return Handler(submission); }
    }
    private static async Task VerifyApprovalCardAsync(Grid root)
    {
        var snapshot = new ChatApproval(new("w", "a"), "session", "approval", 0, "terminal", "build --test",
            "执行构建", DateTimeOffset.UtcNow.AddMinutes(5), ChatApprovalStatus.Pending, [ApprovalChoice.AllowOnce, ApprovalChoice.Deny]);
        var client = new ApprovalClient(); using var card = new ApprovalCard(snapshot, client);
        Grid.SetColumnSpan(card, 2); root.Children.Add(card);
        try { root.UpdateLayout(); await UntilAsync(() => card.IsLoaded); Check(card.ActualHeight > 0, "approval card loads in the native window"); }
        finally { root.Children.Remove(card); }
        card.Reason = "检查构建";
        var completion = new TaskCompletionSource<ChatApproval>(); client.Handler = _ => completion.Task;
        var sending = card.DecideAsync(ApprovalChoice.AllowOnce);
        await card.DecideAsync(ApprovalChoice.Deny);
        Check(client.Calls.Count == 1 && !card.CanAllow && !card.CanDeny && card.Snapshot.Status == ChatApprovalStatus.Pending,
            "approval submission disables double clicks and never optimistically approves");
        completion.SetException(new IOException("回执暂时不可用")); await sending;
        Check(card.CanAllow && !card.CanDeny && card.Error.Length > 0 && card.Reason == "检查构建",
            "failed decision retains reason and only allows retrying its original choice");
        client.Handler = _ => Task.FromResult(snapshot with { Version = 1, Status = ChatApprovalStatus.Approved, AllowedChoices = [] });
        await card.DecideAsync(ApprovalChoice.AllowOnce);
        Check(client.Calls[0] == client.Calls[1] && card.Snapshot.Status == ChatApprovalStatus.Approved && !card.CanAllow,
            "retry keeps decision identity and only authoritative result resolves card");
        using var expired = new ApprovalCard(snapshot with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) }, client);
        await expired.DecideAsync(ApprovalChoice.AllowOnce);
        Check(!expired.CanAllow && !expired.CanDeny && client.Calls.Count == 2, "expired cards cannot submit");
        using var deferred = new ApprovalCard(snapshot with { Status = ChatApprovalStatus.DeferredDependency }, client);
        Check(!deferred.CanAllow && !deferred.CanDeny, "dependency wait cannot be manually promoted to approval");
        using var restricted = new ApprovalCard(snapshot with { AllowedChoices = [ApprovalChoice.Deny] }, client);
        Check(!restricted.CanAllow && restricted.CanDeny, "card only offers Core-provided decisions");
        card.Update(snapshot);
        Check(card.Snapshot.Version == 1, "stale snapshots cannot reopen a resolved card");
        var rejected = false;
        try { card.Update(snapshot with { Role = new("w", "other") }); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "cross-role approval results cannot replace current card");
        rejected = false;
        try { card.Update(card.Snapshot with { Status = ChatApprovalStatus.Pending }); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "same-version status mutation is rejected");
        var lateClient = new ApprovalClient(); var late = new TaskCompletionSource<ChatApproval>(); lateClient.Handler = _ => late.Task;
        var closing = new ApprovalCard(snapshot, lateClient); var inFlight = closing.DecideAsync(ApprovalChoice.AllowOnce);
        closing.Dispose(); await inFlight;
        late.SetResult(snapshot with { Version = 1, Status = ChatApprovalStatus.Approved });
        Check(closing.Snapshot.Status == ChatApprovalStatus.Pending && !closing.CanAllow && closing.Error.Length == 0,
            "disposed approval card cancels waiting and ignores a late receipt");
    }
}
