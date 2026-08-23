namespace PuddingDesktop.Debug;

/// <summary>
/// Pure routing decisions for the Desktop debug reverse proxy. Mirrors the
/// dev-up.py Python proxy semantics (BACKEND_PREFIXES + frontend SPA fallback)
/// so the Desktop debug entry behaves exactly like the dev-up entry:
/// backend-owned prefixes go to Core, everything else to the frontend dev
/// server, and extensionless /admin deep links fall back to the SPA index.
/// </summary>
public static class ProxyRoutePlanner
{
    private static readonly string[] BackendPrefixes =
    {
        "/api",
        "/swagger",
        "/health",
        "/healthz",
        "/metrics",
        "/assets",
        "/connectors",
        "/session-events",
    };

    /// <summary>Headers that must not be forwarded between proxy hops.</summary>
    public static readonly string[] HopByHopHeaders =
    {
        "Connection",
        "Keep-Alive",
        "Proxy-Authenticate",
        "Proxy-Authorization",
        "TE",
        "Trailer",
        "Transfer-Encoding",
        "Upgrade",
    };

    public static bool IsBackendPath(string path)
    {
        foreach (var prefix in BackendPrefixes)
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Effective path+query after applying the frontend SPA fallback for
    /// stateless GET/HEAD deep links: /admin/{route} without a file extension
    /// rewrites to /admin/ (the SPA index). Query string is preserved only
    /// when no rewrite happens, matching dev-up.
    /// </summary>
    public static string GetEffectivePath(string method, string pathAndQuery)
    {
        if (method.Equals("GET", StringComparison.OrdinalIgnoreCase)
            || method.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
        {
            return GetSpaFallbackPath(pathAndQuery);
        }

        return pathAndQuery;
    }

    public static string GetSpaFallbackPath(string pathAndQuery)
    {
        var queryIndex = pathAndQuery.IndexOf('?');
        var path = queryIndex >= 0 ? pathAndQuery[..queryIndex] : pathAndQuery;
        if (path.Length == 0)
            path = "/";

        if (!path.StartsWith("/admin/", StringComparison.Ordinal) || path == "/admin/")
            return pathAndQuery;

        var lastSlash = path.LastIndexOf('/');
        if (path[(lastSlash + 1)..].Contains('.'))
            return pathAndQuery;

        return "/admin/";
    }
}
