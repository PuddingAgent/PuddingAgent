using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace PuddingSlack;

public sealed record SlackMember(string UserId, string UserName, string DisplayName, string RealName, bool IsBot)
{
    public string Mention => $"<@{UserId}>";
}
public sealed record SlackMemberLookup(string Status, bool Complete, IReadOnlyList<SlackMember> Candidates,
    string? Mention, DateTimeOffset ObservedAt, string Scope = "workspace_directory");

/// <summary>Read-only, bounded workspace lookup. A partial scan can never prove unique name resolution.</summary>
public sealed class SlackMemberDirectory(HttpClient http, string botToken, int maxPages = 5)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<SlackMember>? _members;
    private DateTimeOffset _observedAt;

    public async Task<SlackMemberLookup> LookupAsync(string query, CancellationToken ct)
    {
        query = query.Trim().TrimStart('@');
        if (query.Length is 0 or > 100 || query.Any(char.IsControl)) throw new SlackProtocolException("invalid_member_query");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        await _gate.WaitAsync(deadline.Token);
        try
        {
            var complete = true;
            var observed = _observedAt;
            var members = _members;
            if (members is null || DateTimeOffset.UtcNow - observed >= TimeSpan.FromMinutes(5))
            {
                var collected = new Dictionary<string, SlackMember>(StringComparer.Ordinal);
                var seenCursors = new HashSet<string>(StringComparer.Ordinal);
                var cursor = "";
                observed = DateTimeOffset.UtcNow;
                for (var page = 0; page < maxPages; page++)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get,
                        "https://slack.com/api/users.list?limit=200&cursor=" + Uri.EscapeDataString(cursor));
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", botToken);
                    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                    if (response.StatusCode == HttpStatusCode.TooManyRequests) throw new SlackProtocolException("directory_rate_limited");
                    if (!response.IsSuccessStatusCode) throw new SlackProtocolException("directory_http_error");
                    await response.Content.LoadIntoBufferAsync(1024 * 1024, deadline.Token);
                    using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(deadline.Token));
                    var root = json.RootElement;
                    if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
                        throw new SlackProtocolException(SlackEventParser.String(root, "error") == "missing_scope"
                            ? "directory_requires_users_read" : "directory_api_rejected");
                    if (!root.TryGetProperty("members", out var rows) || rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > 200)
                        throw new SlackProtocolException("directory_invalid_response");
                    foreach (var row in rows.EnumerateArray())
                    {
                        if (Flag(row, "deleted")) continue;
                        var id = SlackEventParser.String(row, "id");
                        if (id is null || id.Length < 2 || id[0] is not ('U' or 'W') || !id.All(char.IsAsciiLetterOrDigit)) continue;
                        var profile = row.TryGetProperty("profile", out var p) ? p : default;
                        collected[id] = new(id, Name(row, "name"), Name(profile, "display_name"),
                            Name(profile, "real_name") is { Length: > 0 } real ? real : Name(row, "real_name"), Flag(row, "is_bot"));
                    }
                    cursor = root.TryGetProperty("response_metadata", out var metadata)
                        ? SlackEventParser.String(metadata, "next_cursor") ?? "" : "";
                    if (cursor.Length == 0) break;
                    if (!seenCursors.Add(cursor)) throw new SlackProtocolException("directory_cursor_repeated");
                }
                complete = cursor.Length == 0;
                members = collected.Values.ToArray();
                if (complete) { _members = members; _observedAt = observed; }
            }
            var matches = members.Where(member => new[] { member.UserId, member.UserName, member.DisplayName, member.RealName }
                .Any(name => string.Equals(name, query, StringComparison.OrdinalIgnoreCase))).ToArray();
            var status = !complete ? "incomplete" : matches.Length switch { 0 => "not_found", 1 => "resolved", _ => "ambiguous" };
            return new(status, complete, matches.Take(20).ToArray(), status == "resolved" ? matches[0].Mention : null, observed);
        }
        finally { _gate.Release(); }
    }

    private static bool Flag(JsonElement row, string name) => row.ValueKind == JsonValueKind.Object
        && row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    private static string Name(JsonElement row, string name)
    {
        var value = SlackEventParser.String(row, name) ?? "";
        return value.Length > 200 ? value[..200] : value;
    }
}
