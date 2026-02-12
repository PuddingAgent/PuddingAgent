using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PuddingCode.Configuration;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools;

/// <summary>File-backed store for automatic approval tickets.</summary>
public sealed class FileToolApprovalTicketStore : IToolApprovalTicketStore
{
    private readonly ToolApprovalJsonFileStore<ToolApprovalTicketRecord> _store;

    public FileToolApprovalTicketStore(PuddingDataPaths paths, ILogger<FileToolApprovalTicketStore> logger)
    {
        _store = new ToolApprovalJsonFileStore<ToolApprovalTicketRecord>(
            Path.Combine(paths.RuntimeRoot, "tool-approval", "tickets.json"),
            logger);
    }

    public async Task SaveAsync(ToolApprovalTicketRecord ticket, CancellationToken ct = default)
    {
        await _store.UpdateAsync(tickets =>
        {
            tickets[ticket.TicketId] = ticket;
        }, ct);
    }

    public async Task<ToolApprovalTicketRecord?> GetAsync(string ticketId, CancellationToken ct = default)
    {
        var tickets = await _store.LoadAsync(ct);
        return tickets.GetValueOrDefault(ticketId);
    }

    public async Task<IReadOnlyList<ToolApprovalTicketRecord>> ListAsync(CancellationToken ct = default)
    {
        var tickets = await _store.LoadAsync(ct);
        return tickets.Values
            .OrderBy(t => t.CreatedAtUtc)
            .ToArray();
    }
}

/// <summary>File-backed store for fast automatic approval allowlist rules.</summary>
public sealed class FileToolApprovalAllowlistStore : IToolApprovalAllowlistStore
{
    private readonly ToolApprovalJsonFileStore<ToolApprovalAllowlistRule> _store;

    public FileToolApprovalAllowlistStore(PuddingDataPaths paths, ILogger<FileToolApprovalAllowlistStore> logger)
    {
        _store = new ToolApprovalJsonFileStore<ToolApprovalAllowlistRule>(
            Path.Combine(paths.RuntimeRoot, "tool-approval", "allowlist.json"),
            logger);
    }

    public async Task SaveAsync(ToolApprovalAllowlistRule rule, CancellationToken ct = default)
    {
        await _store.UpdateAsync(rules =>
        {
            foreach (var builtIn in ToolApprovalBuiltInAllowlistRules.Create())
                rules.TryAdd(builtIn.RuleId, builtIn);
            rules[rule.RuleId] = rule;
        }, ct);
    }

    public async Task<ToolApprovalAllowlistRule?> GetAsync(string ruleId, CancellationToken ct = default)
    {
        var rules = await LoadWithBuiltInsAsync(ct);
        return rules.GetValueOrDefault(ruleId);
    }

    public async Task<IReadOnlyList<ToolApprovalAllowlistRule>> ListAsync(CancellationToken ct = default)
    {
        var rules = await LoadWithBuiltInsAsync(ct);
        return rules.Values
            .OrderBy(r => r.Source)
            .ThenBy(r => r.WorkspaceId ?? "")
            .ThenBy(r => r.ToolId)
            .ThenBy(r => r.Command ?? r.ArgumentsJson ?? "")
            .ToArray();
    }

    private async Task<Dictionary<string, ToolApprovalAllowlistRule>> LoadWithBuiltInsAsync(CancellationToken ct)
    {
        var rules = ToolApprovalBuiltInAllowlistRules.Create()
            .ToDictionary(r => r.RuleId, StringComparer.Ordinal);
        foreach (var (ruleId, rule) in await _store.LoadAsync(ct))
            rules[ruleId] = rule;
        return rules;
    }
}

/// <summary>File-backed store for automatic approval audit events.</summary>
public sealed class FileToolApprovalAuditStore : IToolApprovalAuditStore
{
    private readonly string _path;
    private readonly ILogger<FileToolApprovalAuditStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileToolApprovalAuditStore(PuddingDataPaths paths, ILogger<FileToolApprovalAuditStore> logger)
    {
        _path = Path.Combine(paths.RuntimeRoot, "tool-approval", "audit-events.jsonl");
        _logger = logger;
    }

    public async Task SaveAsync(ToolApprovalAuditEvent auditEvent, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var line = JsonSerializer.Serialize(auditEvent, ToolApprovalStoreJson.Options);
            await File.AppendAllTextAsync(_path, line + Environment.NewLine, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ToolApprovalAuditEvent>> ListAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!File.Exists(_path))
                return [];

            var events = new List<ToolApprovalAuditEvent>();
            foreach (var line in await File.ReadAllLinesAsync(_path, ct))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                try
                {
                    var evt = JsonSerializer.Deserialize<ToolApprovalAuditEvent>(line, ToolApprovalStoreJson.Options);
                    if (evt is not null)
                        events.Add(evt);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "[ToolApproval] Skipping malformed audit event in {Path}", _path);
                }
            }

            return events
                .OrderByDescending(e => e.CreatedAtUtc)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }
}

internal sealed class ToolApprovalJsonFileStore<TRecord>
{
    private readonly string _path;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ToolApprovalJsonFileStore(string path, ILogger logger)
    {
        _path = path;
        _logger = logger;
    }

    public async Task<Dictionary<string, TRecord>> LoadAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await LoadUnlockedAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(Dictionary<string, TRecord> records, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await SaveUnlockedAsync(records, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateAsync(Action<Dictionary<string, TRecord>> update, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var records = await LoadUnlockedAsync(ct);
            update(records);
            await SaveUnlockedAsync(records, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Dictionary<string, TRecord>> LoadUnlockedAsync(CancellationToken ct)
    {
        if (!File.Exists(_path))
            return new Dictionary<string, TRecord>(StringComparer.Ordinal);

        try
        {
            var json = await File.ReadAllTextAsync(_path, ct);
            var records = JsonSerializer.Deserialize<IReadOnlyList<TRecord>>(json, ToolApprovalStoreJson.Options)
                          ?? [];
            return records
                .Select(record => (Key: ToolApprovalRecordKey.Get(record), Record: record))
                .Where(item => !string.IsNullOrWhiteSpace(item.Key))
                .ToDictionary(item => item.Key!, item => item.Record, StringComparer.Ordinal);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "[ToolApproval] Failed to read store {Path}; treating it as empty.", _path);
            return new Dictionary<string, TRecord>(StringComparer.Ordinal);
        }
    }

    private async Task SaveUnlockedAsync(Dictionary<string, TRecord> records, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var items = records.Values.ToArray();
        var json = JsonSerializer.Serialize(items, ToolApprovalStoreJson.PrettyOptions);
        await File.WriteAllTextAsync(_path, json, ct);
    }
}

internal static class ToolApprovalRecordKey
{
    public static string? Get<TRecord>(TRecord record) => record switch
    {
        ToolApprovalTicketRecord ticket => ticket.TicketId,
        ToolApprovalAllowlistRule rule => rule.RuleId,
        _ => null,
    };
}

internal static class ToolApprovalStoreJson
{
    public static readonly JsonSerializerOptions Options = Create(writeIndented: false);
    public static readonly JsonSerializerOptions PrettyOptions = Create(writeIndented: true);

    private static JsonSerializerOptions Create(bool writeIndented)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = writeIndented,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
