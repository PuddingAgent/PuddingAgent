using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;
using PuddingCode.Abstractions;
using PuddingCode.Platform;

namespace PuddingRuntime.Services;

public sealed record MemoryWikiPageUpdateRequest
{
    public required string WorkspaceId { get; init; }
    public required string SessionId { get; init; }
    public required SubconsciousMemoryScope MemoryScope { get; init; }
    public required IReadOnlyList<string> MemoryNotes { get; init; }
    public string? AgentId { get; init; }
    public string? AgentTemplateId { get; init; }
    public string? HookEventId { get; init; }
    public string? SubconsciousJobId { get; init; }
    public MemoryLlmConfig? MemoryLlmConfig { get; init; }
}

public sealed record MemoryWikiPageUpdatePlan
{
    public string Schema { get; init; } = MemoryWikiPageUpdateService.Schema;
    public IReadOnlyList<MemoryWikiPageUpdate> Updates { get; init; } = [];
}

public sealed record MemoryWikiPageUpdate
{
    public required string Book { get; init; }
    public required string Page { get; init; }
    public required string Content { get; init; }
}

public sealed record MemoryWikiPageUpdateResult
{
    public required string RawResponse { get; init; }
    public MemoryWikiPageUpdatePlan? Plan { get; init; }
    public required IReadOnlyList<string> Errors { get; init; }
    public bool IsValid => Errors.Count == 0 && Plan is not null;
}

/// <summary>
/// Generates and validates the minimal Memory v2 V1 page-update JSON.
/// It intentionally validates only JSON shape and required fields.
/// </summary>
public sealed class MemoryWikiPageUpdateService
{
    public const string Schema = "pudding.memory_wiki_page_update.v1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IMemoryLlmClient _memoryLlmClient;

    public MemoryWikiPageUpdateService(IMemoryLlmClient memoryLlmClient)
    {
        _memoryLlmClient = memoryLlmClient;
    }

    public async Task<MemoryWikiPageUpdateResult> GenerateAsync(
        MemoryWikiPageUpdateRequest request,
        CancellationToken ct = default)
    {
        ValidateRequest(request);

        var raw = await _memoryLlmClient.ChatWithScopedConfigAsync(
            BuildSystemPrompt(),
            BuildUserPrompt(request),
            request.MemoryLlmConfig,
            request.MemoryScope,
            tools: null,
            ct);

        return ValidateJson(raw);
    }

    public static MemoryWikiPageUpdateResult ValidateJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new MemoryWikiPageUpdateResult
            {
                RawResponse = raw,
                Plan = null,
                Errors = ["empty_json"],
            };
        }

        MemoryWikiPageUpdatePlan? plan;
        try
        {
            plan = JsonSerializer.Deserialize<MemoryWikiPageUpdatePlan>(raw, JsonOptions);
        }
        catch (JsonException)
        {
            return new MemoryWikiPageUpdateResult
            {
                RawResponse = raw,
                Plan = null,
                Errors = ["invalid_json"],
            };
        }

        var errors = new List<string>();
        if (plan is null)
        {
            errors.Add("invalid_json");
        }
        else
        {
            if (!string.Equals(plan.Schema, Schema, StringComparison.Ordinal))
                errors.Add("invalid_schema");
            if (plan.Updates.Count == 0)
                errors.Add("missing_updates");

            foreach (var update in plan.Updates)
            {
                if (string.IsNullOrWhiteSpace(update.Book))
                    errors.Add("missing_book");
                if (string.IsNullOrWhiteSpace(update.Page))
                    errors.Add("missing_page");
                if (string.IsNullOrWhiteSpace(update.Content))
                    errors.Add("missing_content");
            }
        }

        return new MemoryWikiPageUpdateResult
        {
            RawResponse = raw,
            Plan = errors.Count == 0 ? plan : null,
            Errors = errors.Distinct(StringComparer.Ordinal).ToArray(),
        };
    }

    private static void ValidateRequest(MemoryWikiPageUpdateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.WorkspaceId))
            throw new ArgumentException("WorkspaceId is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.SessionId))
            throw new ArgumentException("SessionId is required.", nameof(request));
        if (request.MemoryScope is null)
            throw new ArgumentException("MemoryScope is required.", nameof(request));
        if (!string.Equals(request.MemoryScope.WorkspaceId, request.WorkspaceId, StringComparison.Ordinal))
            throw new ArgumentException("MemoryScope workspace must match request workspace.", nameof(request));
        if (!string.Equals(request.MemoryScope.SessionId, request.SessionId, StringComparison.Ordinal))
            throw new ArgumentException("MemoryScope session must match request session.", nameof(request));
    }

    private static string BuildSystemPrompt() =>
        """
        You are Pudding's subconscious memory page updater.
        Return only strict JSON with schema pudding.memory_wiki_page_update.v1.
        The JSON root must be: {"schema":"pudding.memory_wiki_page_update.v1","updates":[{"book":"...","page":"/...","content":"markdown"}]}.
        Use the supplied memory notes as the primary evidence.
        Do not choose workspace, agent, library, or session scope.
        Do not emit reuse, append, supersede, merge, confidence, quarantine, delete, or tool calls.
        Each content value must be the final markdown body for that target page.
        """;

    private static string BuildUserPrompt(MemoryWikiPageUpdateRequest request)
    {
        var payload = new
        {
            instruction = "Convert memoryNotes into final Wiki page content updates.",
            source = new
            {
                workspaceId = request.WorkspaceId,
                sessionId = request.SessionId,
                hookEventId = request.HookEventId,
                subconsciousJobId = request.SubconsciousJobId,
                agentId = request.AgentId,
                agentTemplateId = request.AgentTemplateId,
                memoryLibraryId = request.MemoryScope.MemoryLibraryId,
            },
            memoryNotes = request.MemoryNotes,
            requiredRootShape = new
            {
                schema = Schema,
                updates = new[]
                {
                    new
                    {
                        book = "Book title",
                        page = "/Page path",
                        content = "# Page path\n\n- Final markdown content.",
                    },
                },
            },
        };
        return JsonSerializer.Serialize(payload, JsonOptions);
    }
}
