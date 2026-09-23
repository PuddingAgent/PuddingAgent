using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntime.Services;

/// <summary>
/// Keeps oversized tool results out of model history without altering their original
/// content. The complete result is stored below the active workspace and the model
/// receives a bounded head/tail preview with a file_read continuation.
/// </summary>
/// <remarks>
/// The context bound is an execution-stability contract, not a best-effort optimisation:
/// <b>every</b> return path of <see cref="MaterializeAsync"/> yields at most
/// <see cref="MaxInlineChars"/> characters. When the durable copy cannot be written the
/// result degrades to a bounded preview carrying the machine-readable
/// <see cref="MaterializationFailureCode"/> notice. Returning the raw payload on failure
/// (the previous behaviour) silently lifted the bound precisely when the spill
/// infrastructure was broken — the failure mode the bound exists to contain. Durability is
/// still best-effort; what is never best-effort is the budget.
/// </remarks>
internal static class ToolResultContextPolicy
{
    internal const int MaxInlineChars = 8 * 1024;

    /// <summary>
    /// Stable, searchable code emitted when no durable copy could be produced. Present in
    /// both the warning log and the bounded payload so callers and diagnostics can find it.
    /// </summary>
    internal const string MaterializationFailureCode = "context_materialization_failed";

    private const string SpillDirectoryName = ".pudding/context-tool-results";
    private const int SpillAttempts = 2;
    private const int MaxFailureReasonChars = 160;
    private const int MaxNoticeTokenChars = 64;

    internal static async Task<string> MaterializeAsync(
        string content,
        string? workingDirectory,
        string sessionId,
        string toolName,
        string toolCallId,
        ILogger logger,
        CancellationToken ct)
    {
        if (content.Length <= MaxInlineChars)
            return content;

        Exception? lastFailure = null;
        for (var attempt = 1; attempt <= SpillAttempts; attempt++)
        {
            try
            {
                return await SpillAsync(content, workingDirectory, sessionId, toolName, toolCallId, logger, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastFailure = ex;
                if (attempt < SpillAttempts)
                {
                    logger.LogWarning(
                        ex,
                        "[AgentExec:ToolResultContext] Spill attempt {Attempt} of {Attempts} failed; retrying tool={Tool} call={Call} chars={Chars}",
                        attempt,
                        SpillAttempts,
                        toolName,
                        toolCallId,
                        content.Length);
                }
            }
        }

        logger.LogWarning(
            lastFailure,
            "[AgentExec:ToolResultContext] {Code} tool={Tool} call={Call} session={Session} chars={Chars} attempts={Attempts} durability=unavailable",
            MaterializationFailureCode,
            toolName,
            toolCallId,
            sessionId,
            content.Length,
            SpillAttempts);

        return BuildUnmaterializedBound(content, lastFailure, sessionId, toolName, toolCallId);
    }

    /// <summary>
    /// Writes the durable copy plus its artifact manifest and returns the bounded preview.
    /// Throws on any durability failure; the caller decides how to degrade.
    /// </summary>
    private static async Task<string> SpillAsync(
        string content,
        string? workingDirectory,
        string sessionId,
        string toolName,
        string toolCallId,
        ILogger logger,
        CancellationToken ct)
    {
        // Use the exact same fallback root as file_read/search_grep. Root chat
        // dispatches commonly omit WorkingDirectory; failing open in that case
        // would silently disable the context bound for the dominant traffic path.
        var workspaceRoot = HostFileToolPaths.ResolveWorkspaceRoot(workingDirectory);
        if (!Directory.Exists(workspaceRoot))
            throw new DirectoryNotFoundException($"Working directory does not exist: {workspaceRoot}");

        var spillRoot = Path.GetFullPath(Path.Combine(workspaceRoot, SpillDirectoryName));
        EnsureDescendant(workspaceRoot, spillRoot);
        Directory.CreateDirectory(spillRoot);
        TryMarkHidden(spillRoot);

        var sessionDirectory = Path.GetFullPath(Path.Combine(spillRoot, SanitizePathSegment(sessionId)));
        EnsureDescendant(spillRoot, sessionDirectory);
        Directory.CreateDirectory(sessionDirectory);

        var fileName = $"{SanitizePathSegment(toolCallId)}-{SanitizePathSegment(toolName)}.txt";
        var spillPath = Path.GetFullPath(Path.Combine(sessionDirectory, fileName));
        EnsureDescendant(sessionDirectory, spillPath);
        await File.WriteAllTextAsync(spillPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct);

        var relativePath = Path.GetRelativePath(workspaceRoot, spillPath).Replace('\\', '/');
        var utf8Bytes = Encoding.UTF8.GetByteCount(content);
        var contentHash = $"sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant()}";
        var lineCount = CountLines(content);
        var manifestPath = spillPath + ".artifact.json";
        var relativeManifestPath = Path.GetRelativePath(workspaceRoot, manifestPath).Replace('\\', '/');
        await File.WriteAllTextAsync(
            manifestPath,
            JsonSerializer.Serialize(new ToolResultArtifactManifest(
                1, "tool_result", "workspace", sessionId, toolName, toolCallId,
                contentHash, content.Length, utf8Bytes, lineCount, relativePath, DateTimeOffset.UtcNow),
                ArtifactJsonOptions),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            ct);
        var notice =
            $"[TOOL RESULT BOUNDED: original_chars={content.Length}; original_utf8_bytes={utf8Bytes}; " +
            $"original_lines={lineCount}; content_sha256={contentHash}; full_output_file={relativePath}; " +
            $"artifact_manifest={relativeManifestPath}; use file_read with offset_lines/limit_lines to continue; " +
            "do not use full_file=true.]";
        var bounded = BuildBoundedPreview(content, notice);

        logger.LogInformation(
            "[AgentExec:ToolResultContext] Spilled oversized tool result tool={Tool} call={Call} originalChars={OriginalChars} inlineChars={InlineChars} path={Path}",
            toolName,
            toolCallId,
            content.Length,
            bounded.Length,
            relativePath);
        return bounded;
    }

    /// <summary>
    /// Bounded degradation used when no durable copy exists. Keeps the model-input bound
    /// intact and carries a machine-readable notice: stable code, tool/session/call
    /// identity, original size/hash and the available recovery options.
    /// </summary>
    internal static string BuildUnmaterializedBound(
        string content,
        Exception? failure,
        string sessionId,
        string toolName,
        string toolCallId)
    {
        var utf8Bytes = Encoding.UTF8.GetByteCount(content);
        var contentHash = $"sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant()}";
        var lineCount = CountLines(content);
        var notice =
            $"[TOOL RESULT BOUNDED: error={MaterializationFailureCode}; reason={DescribeFailure(failure)}; " +
            $"original_chars={content.Length}; original_utf8_bytes={utf8Bytes}; original_lines={lineCount}; " +
            $"content_sha256={contentHash}; session={SanitizeNoticeToken(sessionId)}; " +
            $"tool={SanitizeNoticeToken(toolName)}; call={SanitizeNoticeToken(toolCallId)}; " +
            "full_output_file=UNAVAILABLE; no durable copy was written for this result; " +
            "recovery=retry the tool call or read the underlying artifact directly; " +
            "the complete payload is not available in this context.]";
        return BuildBoundedPreview(content, notice);
    }

    /// <summary>
    /// Head/tail preview whose length is always within <see cref="MaxInlineChars"/>, for any
    /// notice length (an oversized notice is truncated rather than inflating the result).
    /// </summary>
    internal static string BuildBoundedPreview(string content, string notice)
    {
        if (content.Length <= MaxInlineChars)
            return content;

        var separatorChars = 4; // two surrounding blank-line separators
        var noticeBudget = Math.Max(0, MaxInlineChars - separatorChars);
        var boundedNotice = notice.Length > noticeBudget
            ? TruncateWithoutSplittingSurrogates(notice, noticeBudget)
            : notice;
        var previewBudget = Math.Max(0, MaxInlineChars - boundedNotice.Length - separatorChars);
        var headChars = previewBudget * 3 / 4;
        var tailChars = previewBudget - headChars;
        if (headChars > 0 && char.IsHighSurrogate(content[headChars - 1]))
            headChars--;
        var tailStart = content.Length - tailChars;
        if (tailChars > 0 && char.IsLowSurrogate(content[tailStart]))
        {
            tailStart++;
            tailChars--;
        }

        var sb = new StringBuilder(MaxInlineChars);
        sb.Append(content.AsSpan(0, headChars));
        sb.Append("\n\n");
        sb.Append(boundedNotice);
        sb.Append("\n\n");
        sb.Append(content.AsSpan(tailStart, tailChars));
        return sb.ToString();
    }

    private static string DescribeFailure(Exception? failure)
    {
        if (failure is null)
            return "unknown";

        var flattened = new string(failure.Message
            .Select(ch => char.IsControl(ch) || ch is ';' or '[' or ']' ? ' ' : ch)
            .ToArray())
            .Trim();
        if (flattened.Length > MaxFailureReasonChars)
            flattened = TruncateWithoutSplittingSurrogates(flattened, MaxFailureReasonChars);
        return flattened.Length == 0 ? failure.GetType().Name : $"{failure.GetType().Name}: {flattened}";
    }

    private static string SanitizeNoticeToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "unknown";

        var token = new string(value
            .Trim()
            .Select(ch => char.IsControl(ch) || ch is ';' or '[' or ']' or '=' or '/' or '\\' or ' ' ? '_' : ch)
            .ToArray());
        return token.Length > MaxNoticeTokenChars
            ? TruncateWithoutSplittingSurrogates(token, MaxNoticeTokenChars)
            : token;
    }

    private static string TruncateWithoutSplittingSurrogates(string value, int maxChars)
    {
        if (maxChars <= 0)
            return string.Empty;
        if (value.Length <= maxChars)
            return value;

        var length = maxChars;
        if (char.IsHighSurrogate(value[length - 1]))
            length--;
        return value[..length];
    }

    private static void EnsureDescendant(string root, string candidate)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedCandidate = Path.GetFullPath(candidate);
        var prefix = normalizedRoot + Path.DirectorySeparatorChar;
        if (!normalizedCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Tool-result spill path escaped its root: {normalizedCandidate}");
    }

    private static string SanitizePathSegment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "unknown";

        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var sanitized = new string(value
            .Trim()
            .Select(ch => invalid.Contains(ch) || ch is '/' or '\\' ? '_' : ch)
            .Take(96)
            .ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "unknown" : sanitized;
    }

    private static void TryMarkHidden(string path)
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
        }
        catch
        {
            // Cosmetic only. The spill remains excluded from search_grep by name.
        }
    }

    private static int CountLines(string value)
    {
        if (value.Length == 0)
            return 0;

        var count = 1;
        foreach (var ch in value)
        {
            if (ch == '\n')
                count++;
        }

        return count;
    }

    private sealed record ToolResultArtifactManifest(
        int SchemaVersion,
        string Kind,
        string WorkspaceScope,
        string SessionId,
        string ToolName,
        string ToolCallId,
        string ContentSha256,
        int OriginalCharCount,
        int OriginalUtf8Bytes,
        int OriginalLineCount,
        string ContentPath,
        DateTimeOffset CreatedAtUtc);

    private static readonly JsonSerializerOptions ArtifactJsonOptions = new(JsonSerializerDefaults.Web);
}
