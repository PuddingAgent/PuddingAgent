using System.Text.RegularExpressions;

namespace PuddingSecretReferences;

/// <summary>Resolves only credential values, never an entire mutable configuration document.</summary>
public static class CredentialReference
{
    private static readonly Regex EnvironmentReference = new(@"\$\{(?<name>[A-Za-z_][A-Za-z0-9_]*)\}");

    public static async Task<string> ResolveAsync(string? value,
        Func<string, CancellationToken, Task<string>>? inject, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var expanded = EnvironmentReference.Replace(value ?? "", match =>
            Environment.GetEnvironmentVariable(match.Groups["name"].Value)
            ?? throw new InvalidOperationException("Credential environment variable is unavailable."));
        if (!expanded.Contains("{{vault:", StringComparison.OrdinalIgnoreCase)) return expanded;
        if (inject is null) throw new InvalidOperationException("Credential vault resolver is unavailable.");
        string resolved;
        try { resolved = await inject(expanded, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { throw new InvalidOperationException("Credential vault lookup or decryption failed."); }
        if (string.IsNullOrWhiteSpace(resolved) || resolved.Contains("{{vault:", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Credential vault reference could not be resolved.");
        return resolved;
    }
}
