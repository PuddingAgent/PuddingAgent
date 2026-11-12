using System.Text.Json;

namespace PuddingDingTalk;

public interface IGroupReplyJournal
{
    bool TryClaim(GroupSource source, string messageId);
    void Finish(GroupSource source, string messageId, string status);
}

public sealed class DingTalkGroupHandler(string appId, IPublicGroupAgent agent,
    IGroupReplyJournal journal, TimeProvider clock)
{
    private readonly object gate = new();
    private readonly Dictionary<(string Corp, string User), int> users = [];
    private readonly Dictionary<(string Corp, string Room), int> rooms = [];
    private long minute = -1;
    private int total;

    // No generic tool/command dispatcher or private chat store is reachable from this handler.
    public async Task<string> HandleCallbackAsync(string topic, JsonElement body,
        Func<Uri, GroupSource, GroupReply, CancellationToken, Task<bool>> reply, CancellationToken ct)
    {
        if (topic != GroupIngress.Topic) return "ignored";
        var (ev, reason) = GroupIngress.Verify(body, appId, clock.GetUtcNow());
        // The first failing rule is surfaced so a rejected callback is diagnosable from the log alone.
        if (ev is null) return "source_denied:" + reason;
        var source = ev.Question.Source;
        if (source.PuddingGroup != "guest") return "source_denied";
        if (!Admit(source)) return "rate_limited";
        if (!journal.TryClaim(source, ev.MessageId)) return "duplicate";
        var answer = new GroupReply(PublicGroupAnswers.TooLong);
        if (!ev.Oversized)
        {
            try { answer = await agent.ReplyAsync(ev.Question, ct); }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                answer = new("本次处理未取得完整回执，请稍后重试；如含图片，请确认图片已脱敏。");
            }
        }
        // The durable claim precedes HTTP. An ambiguous send is never automatically repeated.
        var status = "unknown";
        try
        {
            if (await reply(ev.ReplyUrl, source, answer, ct)) status = "sent";
        }
        finally { journal.Finish(source, ev.MessageId, status); }
        return status;
    }

    private bool Admit(GroupSource source)
    {
        lock (gate)
        {
            var current = source.ReceivedAt.ToUnixTimeSeconds() / 60;
            if (current != minute)
            {
                minute = current;
                total = 0;
                users.Clear();
                rooms.Clear();
            }
            var user = (source.CorporationId, source.UserId);
            var room = (source.CorporationId, source.ConversationId);
            if (total >= 24 || users.GetValueOrDefault(user) >= 4 || rooms.GetValueOrDefault(room) >= 12)
                return false;
            total++;
            users[user] = users.GetValueOrDefault(user) + 1;
            rooms[room] = rooms.GetValueOrDefault(room) + 1;
            return true;
        }
    }
}
