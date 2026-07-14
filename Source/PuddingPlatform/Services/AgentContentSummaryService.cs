using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Platform;

namespace PuddingPlatform.Services;

public sealed class AgentContentSummaryService(
    PuddingDataPaths paths,
    ISubconsciousTextProcessingService textProcessing) : IAgentContentSummaryService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public async Task<AgentContentSummaryResult> UpdateAsync(
        AgentContentSummaryUpdateRequest request,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AgentInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Day);

        var memoryRoot = paths.AgentInstanceMemoryRoot(request.AgentInstanceId);
        Directory.CreateDirectory(memoryRoot);

        var contentPath = paths.AgentInstanceContentSummaryFile(request.AgentInstanceId);
        var metadataPath = GetMetadataPath(request.AgentInstanceId);
        var existingMetadata = await ReadMetadataAsync(request.AgentInstanceId, ct);
        var sameDay = string.Equals(existingMetadata?.Day, request.Day, StringComparison.Ordinal);
        var existingSummary = sameDay && File.Exists(contentPath)
            ? await File.ReadAllTextAsync(contentPath, Encoding.UTF8, ct)
            : string.Empty;

        var conversationText = BuildConversationText(existingSummary, request.ConversationText);
        var sourceHash = ComputeSha256($"{request.Day}\n{conversationText}");
        var summary = await textProcessing.SummarizeCurrentSessionAsync(
            new CurrentSessionSummaryRequest(
                request.WorkspaceId,
                request.AgentInstanceId,
                request.AgentTemplateId,
                request.SessionId,
                conversationText,
                request.Reason,
                request.MemoryLlmConfig),
            ct);

        await File.WriteAllTextAsync(contentPath, summary.Trim(), Encoding.UTF8, ct);

        var metadata = new AgentContentSummaryMetadata(
            request.AgentInstanceId,
            request.Day,
            request.SessionId,
            request.Reason,
            sourceHash,
            DateTimeOffset.UtcNow);

        await File.WriteAllTextAsync(
            metadataPath,
            JsonSerializer.Serialize(metadata, JsonOptions),
            Encoding.UTF8,
            ct);

        return new AgentContentSummaryResult(
            request.AgentInstanceId,
            request.Day,
            contentPath,
            metadataPath,
            sourceHash,
            ResetForNewDay: !sameDay && existingMetadata is not null);
    }

    public async Task<AgentContentSummaryResult> SaveCompressedSummaryAsync(
        AgentCompressedContentSummaryRequest request,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AgentInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Day);

        var memoryRoot = paths.AgentInstanceMemoryRoot(request.AgentInstanceId);
        Directory.CreateDirectory(memoryRoot);

        var contentPath = paths.AgentInstanceContentSummaryFile(request.AgentInstanceId);
        var metadataPath = GetMetadataPath(request.AgentInstanceId);
        var existingMetadata = await ReadMetadataAsync(request.AgentInstanceId, ct);
        var sameDay = string.Equals(existingMetadata?.Day, request.Day, StringComparison.Ordinal);
        var existingSummary = sameDay && File.Exists(contentPath)
            ? await File.ReadAllTextAsync(contentPath, Encoding.UTF8, ct)
            : string.Empty;

        var content = BuildCompressedContent(existingSummary, request);
        var sourceHash = ComputeSha256($"{request.Day}\n{content}");
        await File.WriteAllTextAsync(contentPath, content, Encoding.UTF8, ct);

        var metadata = new AgentContentSummaryMetadata(
            request.AgentInstanceId,
            request.Day,
            request.SessionId,
            request.Reason,
            sourceHash,
            DateTimeOffset.UtcNow);

        await File.WriteAllTextAsync(
            metadataPath,
            JsonSerializer.Serialize(metadata, JsonOptions),
            Encoding.UTF8,
            ct);

        return new AgentContentSummaryResult(
            request.AgentInstanceId,
            request.Day,
            contentPath,
            metadataPath,
            sourceHash,
            ResetForNewDay: !sameDay && existingMetadata is not null);
    }

    public async Task<AgentContentSummaryMetadata?> ReadMetadataAsync(
        string agentInstanceId,
        CancellationToken ct = default)
    {
        var metadataPath = GetMetadataPath(agentInstanceId);
        if (!File.Exists(metadataPath))
            return null;

        try
        {
            await using var stream = File.OpenRead(metadataPath);
            return await JsonSerializer.DeserializeAsync<AgentContentSummaryMetadata>(stream, JsonOptions, ct);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<string> ReadContentAsync(
        string agentInstanceId,
        string day,
        CancellationToken ct = default)
    {
        var metadata = await ReadMetadataAsync(agentInstanceId, ct);
        if (!string.Equals(metadata?.Day, day, StringComparison.Ordinal))
            return string.Empty;

        var contentPath = paths.AgentInstanceContentSummaryFile(agentInstanceId);
        return File.Exists(contentPath)
            ? await File.ReadAllTextAsync(contentPath, Encoding.UTF8, ct)
            : string.Empty;
    }

    private string GetMetadataPath(string agentInstanceId) =>
        Path.Combine(paths.AgentInstanceMemoryRoot(agentInstanceId), "content.meta.json");

    private static string BuildConversationText(string existingSummary, string conversationText)
    {
        if (string.IsNullOrWhiteSpace(existingSummary))
            return conversationText.Trim();

        return $"""
已有当天滚动摘要：
{existingSummary.Trim()}

本次新增会话内容：
{conversationText.Trim()}
""";
    }

    private static string BuildCompressedContent(
        string existingSummary,
        AgentCompressedContentSummaryRequest request)
    {
        var section = $"""
## Session {request.SessionId}

Reason: {request.Reason}

{request.SummaryMarkdown.Trim()}
""";

        if (string.IsNullOrWhiteSpace(existingSummary))
        {
            return $"""
# {request.Day} 当前摘要

{section}
""".Trim();
        }

        return $"""
{existingSummary.Trim()}

{section}
""".Trim();
    }

    private static string ComputeSha256(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

public sealed record AgentContentSummaryUpdateRequest(
    string WorkspaceId,
    string AgentInstanceId,
    string? AgentTemplateId,
    string SessionId,
    string Day,
    string ConversationText,
    string Reason,
    MemoryLlmConfig? MemoryLlmConfig);

public sealed record AgentContentSummaryMetadata(
    string AgentInstanceId,
    string Day,
    string LastSessionId,
    string LastReason,
    string SourceHash,
    DateTimeOffset UpdatedAt);
