using System;
using System.Net;
using System.Text.Json;

namespace PuddingDesktop.Foundation;

/// <summary>How a single attempt to read a panel canvas from Core ended.</summary>
public enum PanelCanvasFeedStatus
{
    /// <summary>Core returned a snapshot (content included).</summary>
    Fetched,

    /// <summary>Core answered 404: this canvas has never been published. Not an error.</summary>
    NotPublished,

    /// <summary>Core could not be reached, or answered with something unusable.</summary>
    Unreachable,
}

/// <summary>One panel snapshot as Core serves it (<c>GET /api/panels/{id}</c>).</summary>
/// <param name="PanelId">Canvas identity, as Core spells it.</param>
/// <param name="Title">Optional human title; blank is normalized to <c>null</c>.</param>
/// <param name="Revision">Monotonic version. The only safe signal for "content changed".</param>
/// <param name="Content">Agent-authored HTML. Must be rendered inside a sandbox, never injected raw.</param>
public sealed record PanelCanvasFeedItem(
    string PanelId,
    string? Title,
    long Revision,
    string Content);

/// <summary>
/// Outcome of one poll. <see cref="ErrorMessage"/> is only set for
/// <see cref="PanelCanvasFeedStatus.Unreachable"/> and is meant for a human, not for branching.
/// </summary>
public sealed record PanelCanvasFeedResult(
    PanelCanvasFeedStatus Status,
    PanelCanvasFeedItem? Item,
    string? ErrorMessage)
{
    public static PanelCanvasFeedResult Fetched(PanelCanvasFeedItem item)
        => new(PanelCanvasFeedStatus.Fetched, item, null);

    public static PanelCanvasFeedResult NotPublished()
        => new(PanelCanvasFeedStatus.NotPublished, null, null);

    public static PanelCanvasFeedResult Unreachable(string message)
        => new(PanelCanvasFeedStatus.Unreachable, null, message);
}

/// <summary>
/// Reads a panel canvas from Core's HTTP endpoint.
///
/// <para>
/// <b>Why the Shell pulls instead of being pushed to.</b> Panel content now belongs to
/// Core (<c>IPanelCanvasStore</c>) precisely so that a broken transport does not mean
/// "the panel cannot be controlled". The capability channel is the fast path that also
/// raises focus; this feed is the slow path that keeps a canvas correct even when the
/// channel is down, and it is what makes "Agent wrote new content" observable at all.
/// </para>
/// <para>
/// Deliberately BCL-only and side-effect free: URL building, parsing and the
/// re-render decision live here so they can be tested without a window, a WebView2
/// or a running Core. The Shell supplies the transport and the timer.
/// </para>
/// </summary>
public static class PanelCanvasFeed
{
    /// <summary>Route of the read-only canvas endpoint (see <c>PanelCanvasApiController</c>).</summary>
    public const string Route = "/api/panels/";

    /// <summary>Absolute path for one canvas. Percent-encoding keeps a hostile id from escaping the route.</summary>
    public static string BuildPath(string panelId)
        => Route + Uri.EscapeDataString(panelId ?? string.Empty);

    /// <summary>Absolute URI of one canvas relative to the workbench address Core advertises.</summary>
    /// <exception cref="ArgumentException"><paramref name="workbenchAddress"/> is not absolute.</exception>
    public static Uri BuildUri(Uri workbenchAddress, string panelId)
    {
        ArgumentNullException.ThrowIfNull(workbenchAddress);
        if (!workbenchAddress.IsAbsoluteUri)
        {
            throw new ArgumentException(
                "workbenchAddress must be absolute.", nameof(workbenchAddress));
        }

        return new Uri(workbenchAddress, BuildPath(panelId));
    }

    /// <summary>
    /// Parse the JSON body of a canvas snapshot.
    ///
    /// <para>
    /// Strict on purpose: a body without <c>revision</c> or <c>content</c> is rejected rather
    /// than defaulted. A defaulted revision would make the canvas silently stop refreshing,
    /// which is harder to notice than an explicit failure.
    /// </para>
    ///
    /// <para>
    /// When <paramref name="expectedPanelId"/> is supplied, a body describing a **different**
    /// canvas is rejected. This response crosses a process boundary: a mis-routed request,
    /// a proxy cache collision or a store keyed wrongly would otherwise be rendered into
    /// whichever canvas asked — and a canvas silently showing another canvas's content has
    /// no visible symptom at all.
    /// </para>
    /// </summary>
    public static bool TryParse(
        string? json,
        out PanelCanvasFeedItem item,
        out string? error,
        string? expectedPanelId = null)
    {
        item = null!;
        error = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "response body was empty.";
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            error = $"response body was not valid JSON: {ex.Message}";
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = $"expected a JSON object, got {root.ValueKind}.";
                return false;
            }

            var panelId = ReadString(root, "panelId");
            if (string.IsNullOrWhiteSpace(panelId))
            {
                error = "response is missing 'panelId'.";
                return false;
            }

            var resolvedPanelId = panelId.Trim();
            if (expectedPanelId is not null
                && !string.Equals(resolvedPanelId, expectedPanelId, StringComparison.Ordinal))
            {
                error = $"response is for panel '{resolvedPanelId}', expected '{expectedPanelId}'.";
                return false;
            }

            if (!root.TryGetProperty("revision", out var revisionElement)
                || revisionElement.ValueKind != JsonValueKind.Number
                || !revisionElement.TryGetInt64(out var revision))
            {
                error = "response is missing a numeric 'revision'.";
                return false;
            }

            if (!root.TryGetProperty("content", out var contentElement)
                || contentElement.ValueKind != JsonValueKind.String)
            {
                error = "response is missing string 'content'.";
                return false;
            }

            var title = ReadString(root, "title");
            item = new PanelCanvasFeedItem(
                resolvedPanelId,
                string.IsNullOrWhiteSpace(title) ? null : title.Trim(),
                revision,
                contentElement.GetString()!);
            return true;
        }
    }

    /// <summary>
    /// Decide whether a poll result changes what is on screen.
    ///
    /// <para>
    /// Uses <c>!=</c> rather than <c>&gt;</c> on purpose. Revisions only climb while Core keeps
    /// running; after a Core restart the store is empty again and the next publish starts at 1.
    /// A "greater than" test would then ignore a lower revision and the canvas would freeze on
    /// stale content for the rest of the session.
    /// </para>
    /// </summary>
    public static bool RequiresRerender(long renderedRevision, long incomingRevision)
        => renderedRevision != incomingRevision;

    /// <summary>Human-readable label for a failed poll; keeps the caller from inventing wording.</summary>
    public static string DescribeFailure(PanelCanvasFeedStatus status, string? errorMessage) => status switch
    {
        PanelCanvasFeedStatus.NotPublished => "Agent 还没有向这块画布推送内容。",
        PanelCanvasFeedStatus.Unreachable => string.IsNullOrWhiteSpace(errorMessage)
            ? "无法从 Core 读取面板内容。"
            : $"无法从 Core 读取面板内容：{errorMessage}",
        _ => "面板内容已就绪。",
    };

    /// <summary>Map an HTTP status code to a feed status. Kept here so the Shell has no policy.</summary>
    public static PanelCanvasFeedStatus StatusForHttpCode(HttpStatusCode code) => code switch
    {
        HttpStatusCode.NotFound => PanelCanvasFeedStatus.NotPublished,
        _ when (int)code is >= 200 and < 300 => PanelCanvasFeedStatus.Fetched,
        _ => PanelCanvasFeedStatus.Unreachable,
    };

    private static string? ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
}
