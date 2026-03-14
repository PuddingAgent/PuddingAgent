using PuddingCode.Abstractions;

namespace PuddingCodeCLI;

internal sealed record HookRuntimeBundle(
    HookRuntimeMetrics Metrics,
    IReadOnlyList<IAgentHook> Hooks,
    IReadOnlyList<string> EnabledHooks,
    string? AuditFilePath,
    IReadOnlyList<string> ExternalHookNames);

internal static class HookRegistry
{
    public static HookRuntimeBundle Build(string projectRoot, HookConfig config)
    {
        var enabled = Normalize(config.Enabled);
        var hooks = new List<IAgentHook>();
        var metrics = new HookRuntimeMetrics();
        string? auditPath = null;

        if (enabled.Contains("metrics"))
            hooks.Add(metrics);

        if (enabled.Contains("audit_file"))
        {
            auditPath = ResolvePath(projectRoot, config.AuditLogPath);
            hooks.Add(new HookAuditFileHook(auditPath));
        }

        var externalNames = new List<string>();
        if (enabled.Contains("external"))
        {
            foreach (var ext in config.External.Where(e => e.Enabled))
            {
                if (string.IsNullOrWhiteSpace(ext.Command))
                    continue;
                var hookName = string.IsNullOrWhiteSpace(ext.Name) ? ext.Command : ext.Name;
                hooks.Add(new HookExternalProcessHook(hookName, ext.Command, ext.Arguments, ext.TimeoutMs));
                externalNames.Add(hookName);
            }
        }

        return new HookRuntimeBundle(
            Metrics: metrics,
            Hooks: hooks,
            EnabledHooks: enabled.ToList(),
            AuditFilePath: auditPath,
            ExternalHookNames: externalNames);
    }

    private static HashSet<string> Normalize(IEnumerable<string> raw)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in raw)
        {
            if (string.IsNullOrWhiteSpace(item))
                continue;
            set.Add(item.Trim().ToLowerInvariant());
        }

        if (set.Count == 0)
            set.Add("metrics");

        return set;
    }

    private static string ResolvePath(string projectRoot, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            path = ".pudding/hooks.log";

        var p = Environment.ExpandEnvironmentVariables(path.Trim());
        if (p.StartsWith("~"))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var rest = p[1..].TrimStart('/', '\\');
            p = string.IsNullOrEmpty(rest) ? home : Path.Combine(home, rest);
        }

        p = p.Replace('\\', Path.DirectorySeparatorChar)
             .Replace('/', Path.DirectorySeparatorChar);

        return Path.IsPathRooted(p)
            ? Path.GetFullPath(p)
            : Path.GetFullPath(Path.Combine(projectRoot, p));
    }
}
