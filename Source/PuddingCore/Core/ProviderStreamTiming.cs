using System.Diagnostics;

namespace PuddingCode.Core;

/// <summary>
/// Captures the two provider-side boundaries that accurate TTFT attribution needs:
/// the moment the HTTP request is handed to the transport (dispatch) and the moment
/// the response headers arrive. It exists so gateways can report elapsed time from a
/// real dispatch instead of from a local stopwatch that also contains request building.
/// </summary>
public sealed class ProviderStreamTiming
{
    private long _dispatchStartedAt;
    private long _headersReceivedAt;

    /// <summary>Timestamp of the most recent HTTP dispatch, or null when no dispatch happened yet.</summary>
    public long? DispatchStartedAt => _dispatchStartedAt == 0 ? null : _dispatchStartedAt;

    /// <summary>Timestamp of the response headers for the most recent dispatch, if observed.</summary>
    public long? HeadersReceivedAt => _headersReceivedAt == 0 ? null : _headersReceivedAt;

    /// <summary>Mark the moment the request is handed to the transport.</summary>
    public void MarkDispatch()
    {
        _dispatchStartedAt = Stopwatch.GetTimestamp();
        _headersReceivedAt = 0;
    }

    /// <summary>Mark the moment response headers were received for the current dispatch.</summary>
    public void MarkHeaders()
    {
        if (_dispatchStartedAt != 0)
            _headersReceivedAt = Stopwatch.GetTimestamp();
    }

    /// <summary>Elapsed milliseconds from dispatch to response headers, or null when unknown.</summary>
    public long? HeadersMs => _dispatchStartedAt == 0 || _headersReceivedAt == 0
        ? null
        : ElapsedMilliseconds(_dispatchStartedAt, _headersReceivedAt);

    /// <summary>Elapsed milliseconds since the current dispatch, or null when no dispatch happened.</summary>
    public long? SinceDispatchMs => _dispatchStartedAt == 0
        ? null
        : ElapsedMilliseconds(_dispatchStartedAt, Stopwatch.GetTimestamp());

    internal static long ElapsedMilliseconds(long startedAt, long endedAt)
        => (long)((endedAt - startedAt) * 1000.0 / Stopwatch.Frequency);
}
