using System.Security.Cryptography;
using System.Text;
using PuddingRuntime.Services.Skills;

namespace PuddingRuntime.Services.AgentLoop;

/// <summary>
/// Detects an unchanged failure outcome for the same canonical tool and arguments.
/// The second identical failure is surfaced as execution_stalled; later identical attempts
/// are blocked without invoking the underlying tool again.
/// </summary>
internal sealed class FailedToolCallTracker
{
    private readonly Dictionary<string, FailureState> _failures = new(StringComparer.Ordinal);

    internal SkillResult Observe(string canonicalCallKey, SkillResult result)
    {
        if (result.Success)
        {
            _failures.Remove(canonicalCallKey);
            return result;
        }

        var signature = ComputeFailureSignature(result);
        var count = _failures.TryGetValue(canonicalCallKey, out var previous)
                    && previous.Signature == signature
            ? previous.Count + 1
            : 1;
        _failures[canonicalCallKey] = new FailureState(signature, count, result.Error);

        return count >= 2
            ? MarkStalled(result, count)
            : result;
    }

    internal bool TryCreateBlockedResult(string canonicalCallKey, out SkillResult result)
    {
        if (!_failures.TryGetValue(canonicalCallKey, out var failure) || failure.Count < 2)
        {
            result = null!;
            return false;
        }

        result = MarkStalled(new SkillResult
        {
            Success = false,
            Output = string.Empty,
            Error = failure.LastError,
            ExitCode = 1,
        }, failure.Count);
        return true;
    }

    private static SkillResult MarkStalled(SkillResult result, int failureCount)
    {
        var metadata = result.Metadata is null
            ? new Dictionary<string, object>()
            : new Dictionary<string, object>(result.Metadata);
        metadata["runtime_status"] = "execution_stalled";
        metadata["unchanged_failure_count"] = failureCount;

        var lastError = string.IsNullOrWhiteSpace(result.Error)
            ? "The tool returned the same failed outcome."
            : result.Error.Trim();
        return result with
        {
            Error = "execution_stalled: the same canonical tool call failed twice with " +
                    "an unchanged result. Do not retry it unchanged; inspect the target/postcondition " +
                    $"and change strategy. Last error: {lastError}",
            Metadata = metadata,
        };
    }

    private static string ComputeFailureSignature(SkillResult result)
    {
        var output = result.Output ?? string.Empty;
        var error = result.Error ?? string.Empty;
        var material = string.Join(
            '\u001f',
            result.ExitCode.ToString(),
            output.Length.ToString(),
            TakeBounded(output),
            error.Length.ToString(),
            TakeBounded(error));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    private static string TakeBounded(string value)
        => value.Length <= 4_096
            ? value
            : value[..2_048] + value[^2_048..];

    private sealed record FailureState(string Signature, int Count, string? LastError);
}
