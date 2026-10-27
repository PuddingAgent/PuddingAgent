using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Diagnostics;
using PuddingCode.Models;

namespace PuddingRuntime.Services.Diagnostics;

/// <summary>Bounded, local-only compaction evidence. A write failure never changes the execution outcome.</summary>
public sealed class CompactionDiagnosticJournal(PuddingDataPaths paths, IKeyVaultService vault,
    ILogger<CompactionDiagnosticJournal> logger)
{
    internal const long MaxCaseBytes = 8 * 1024 * 1024;
    internal const int MaxCases = 32;
    private const int TerminalReserveBytes = 64 * 1024;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _directory = Path.Combine(paths.DiagnosticsLogsRoot, "compaction");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly Regex SensitiveAssignment = new(
        "(?:api[_-]?key|password|secret|authorization|access[_-]?token|control[_-]?token|cookie)\\s*[\\\"']?\\s*[:=]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex UrlWithQuery = new(@"https?://[^\s""'<>]+\?[^\s""'<>]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    // Full request shape (including retained messages), without user or reasoning bodies.
    internal static object DescribeRequest(IReadOnlyList<ChatMessage> messages) => messages.Select((m, index) => new
    {
        index, role = m.Role.ToString(), providerRole = (m.IsContextSummary ? ChatRole.User : m.Role).ToString(),
        m.IsContextSummary, identity = WarmPrefixCompaction.ComputeMessageIdentity(m),
        contentChars = m.Content?.Length ?? 0, reasoningChars = m.ReasoningContent?.Length ?? 0,
        toolCallIds = m.ToolCalls?.Select(call => call.Id).ToArray(), m.ToolCallId,
        continuationProtocol = m.ContinuationState?.Protocol,
        continuationItems = m.ContinuationState?.OutputItemsJson.Count ?? 0,
    }).ToArray();

    internal async Task<string?> BeginAsync(string compactionId, object plan,
        IReadOnlyList<ChatMessage> source, CancellationToken ct)
    {
        var name = $"{DateTime.UtcNow:yyyyMMdd}-{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(compactionId)))}.jsonl";
        var ok = await AppendAsync(name, "plan", new
        {
            compactionId, plan,
            source = source.Select((m, index) => new
            {
                index, identity = WarmPrefixCompaction.ComputeMessageIdentity(m), role = m.Role.ToString(), m.IsContextSummary,
                m.Content, m.ToolCallId, m.ToolName, m.ToolCalls,
                images = ChatMessageMultimodalNormalizer.GetImageParts(m).Select(image => image.ArtifactId),
                // Keep fingerprints/counts for reasoning/continuation; do not duplicate their bodies.
                reasoningChars = m.ReasoningContent?.Length ?? 0,
                continuationItems = m.ContinuationState?.OutputItemsJson.Count ?? 0,
            }),
        }, ct);
        await TrimAsync(name);
        return ok ? name : null;
    }

    internal async Task<bool> AppendAsync(string? name, string kind, object detail, CancellationToken ct)
    {
        if (name is null) return false;
        try
        {
            if (Path.GetFileName(name) != name) throw new ArgumentException("Evidence filename must be local.");
            var node = JsonSerializer.SerializeToNode(detail, Json)!;
            await RedactAsync(node, ct);
            var line = JsonSerializer.Serialize(new
            {
                schema = "pudding.compaction-diagnostic/1", atUtc = DateTimeOffset.UtcNow, kind, detail = node,
            }, Json) + "\n";
            await _gate.WaitAsync(ct);
            try
            {
                Directory.CreateDirectory(_directory);
                var path = Path.Combine(_directory, name);
                var length = File.Exists(path) ? new FileInfo(path).Length : 0;
                var limit = kind is "outcome" or "interrupted" ? MaxCaseBytes : MaxCaseBytes - TerminalReserveBytes;
                if (length + Encoding.UTF8.GetByteCount(line) > limit - 1024)
                {
                    // Reserve one explicit omission marker; don't silently claim a complete snapshot.
                    if (length < limit - 1024)
                        await File.AppendAllTextAsync(path, JsonSerializer.Serialize(new
                        { schema = "pudding.compaction-diagnostic/1", kind = "capture_limit", omittedKind = kind, limitBytes = MaxCaseBytes }) + "\n", ct);
                    return false;
                }
                await File.AppendAllTextAsync(path, line, ct);
                return true;
            }
            finally { _gate.Release(); }
        }
        catch (Exception ex)
        {
            logger.LogWarning("[CompactionDiagnostic] Capture failed kind={Kind} errorType={ErrorType}", kind, ex.GetType().Name);
            return false;
        }
    }

    private async Task RedactAsync(JsonNode node, CancellationToken ct)
    {
        var values = new List<(JsonNode Parent, string? Key, int Index, string Text)>();
        Collect(node);
        // Strip once per record: the vault implementation loads/decrypts its key set on each call.
        // Joining unescaped strings also catches known secrets containing quotes or newlines.
        if (values.Count == 0) return;
        var separator = "\0" + Guid.NewGuid().ToString("N") + "\0";
        var stripped = await vault.StripAsync(string.Join(separator, values.Select(v => v.Text)), ct);
        var texts = stripped.Split(separator, StringSplitOptions.None);
        if (texts.Length != values.Count) throw new InvalidOperationException("Diagnostic redaction boundary changed.");
        for (var i = 0; i < values.Count; i++)
        {
            var text = texts[i];
            if (DiagnosticEvidenceRedactor.LooksLikeSecret(text) || SensitiveAssignment.IsMatch(text)
                || text.Contains("data:image/", StringComparison.OrdinalIgnoreCase))
                text = "[redacted diagnostic content]";
            else text = UrlWithQuery.Replace(text, "[redacted URL query]");
            var item = values[i];
            if (item.Key is null) item.Parent[item.Index] = text;
            else item.Parent[item.Key] = text;
        }

        void Collect(JsonNode current)
        {
            if (current is JsonObject obj)
            {
                foreach (var pair in obj.ToArray())
                {
                    var key = pair.Key.Replace("_", "").Replace("-", "").ToLowerInvariant();
                    if (key is "apikey" or "password" or "secret" or "authorization" or "accesstoken" or "controltoken" or "cookie")
                        obj[pair.Key] = "[redacted]";
                    else if (pair.Value is JsonValue value && value.TryGetValue<string>(out var text))
                        values.Add((obj, pair.Key, 0, text));
                    else if (pair.Value is not null) Collect(pair.Value);
                }
            }
            else if (current is JsonArray array)
                for (var i = 0; i < array.Count; i++)
                    if (array[i] is JsonValue value && value.TryGetValue<string>(out var text))
                        values.Add((array, null, i, text));
                    else if (array[i] is { } item) Collect(item);
        }
    }

    private async Task TrimAsync(string current)
    {
        await _gate.WaitAsync();
        try
        {
            var files = new DirectoryInfo(_directory).GetFiles("*.jsonl")
                .OrderByDescending(f => f.LastWriteTimeUtc).ToArray();
            for (var i = 0; i < files.Length; i++)
                if (files[i].Name != current && (i >= MaxCases || files[i].LastWriteTimeUtc < DateTime.UtcNow.AddDays(-7)))
                    files[i].Delete();
        }
        catch (Exception ex)
        {
            logger.LogWarning("[CompactionDiagnostic] Retention failed errorType={ErrorType}", ex.GetType().Name);
        }
        finally { _gate.Release(); }
    }
}
