using PuddingCode.Configuration;

namespace PuddingHost.Hosting;

/// <summary>
/// Resolves and prepares the data root directory:
///   - Parse --data-root from args or PUDDING_DATA_ROOT env var
///   - Copy missing default-data files
///   - Create runtime directories
///   - Create default Agent instance if missing
/// </summary>
public static class PuddingDataRootBootstrapper
{
    /// <summary>
    /// Resolve DataRoot from args, env, or fallback to app base/data.
    /// </summary>
    public static string ResolveDataRoot(string[] args)
    {
        var fromArgs = GetDataRoot(args);
        if (fromArgs is not null)
            return fromArgs;

        var fromEnv = Environment.GetEnvironmentVariable("PUDDING_DATA_ROOT");
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return fromEnv;

        return Path.Combine(AppContext.BaseDirectory, "data");
    }

    /// <summary>
    /// Copy default-data files from app base to DataRoot when target files are missing.
    /// Create runtime directories.
    /// </summary>
    public static PuddingDataPaths Bootstrap(string dataRoot)
    {
        var dataPaths = PuddingDataPaths.FromRoot(dataRoot);
        var defaultDataDir = Path.Combine(AppContext.BaseDirectory, "default-data");

        EnsureDefaultData(dataPaths.DataRoot, defaultDataDir);
        EnsureRuntimeDirectories(dataPaths);
        EnsureDefaultAgentInstance(dataPaths);

        return dataPaths;
    }

    private static string? GetDataRoot(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.StartsWith("--data-root=", StringComparison.OrdinalIgnoreCase))
            {
                var value = arg["--data-root=".Length..];
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }

            if (arg.Equals("--data-root", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                var value = args[i + 1];
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }

        return null;
    }

    private static void EnsureDefaultData(string dataRoot, string defaultDataRoot)
    {
        Directory.CreateDirectory(dataRoot);

        if (!Directory.Exists(defaultDataRoot))
            return;

        CopyMissingFiles(defaultDataRoot, dataRoot, relative =>
            !relative.StartsWith("agent-template-presets", StringComparison.OrdinalIgnoreCase));
    }

    private static void CopyMissingFiles(string sourceRoot, string targetRoot, Func<string, bool>? shouldCopy = null)
    {
        foreach (var directory in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceRoot, directory);
            if (shouldCopy is not null && !shouldCopy(relative))
                continue;

            Directory.CreateDirectory(Path.Combine(targetRoot, relative));
        }

        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceRoot, file);
            if (shouldCopy is not null && !shouldCopy(relative))
                continue;

            var target = Path.Combine(targetRoot, relative);
            if (File.Exists(target))
                continue;

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private static void EnsureRuntimeDirectories(PuddingDataPaths paths)
    {
        Directory.CreateDirectory(paths.ConfigRoot);
        Directory.CreateDirectory(paths.AgentTemplatesRoot);
        Directory.CreateDirectory(paths.AgentInstancesRoot);
        Directory.CreateDirectory(paths.WorkspacesRoot);
        Directory.CreateDirectory(paths.SystemLogsRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.ErrorLogFile)!);
        Directory.CreateDirectory(paths.DiagnosticsLogsRoot);
        Directory.CreateDirectory(paths.SessionLogsRoot);
        Directory.CreateDirectory(paths.RuntimeTracesRoot);
        Directory.CreateDirectory(paths.EventQueueRoot);
        Directory.CreateDirectory(paths.MemoryRoot);
        Directory.CreateDirectory(paths.DatabasesRoot);
        Directory.CreateDirectory(paths.BackupsRoot);
        Directory.CreateDirectory(paths.TempRoot);
    }

    /// <summary>
    /// Ensure default Agent instance exists (idempotent).
    /// </summary>
    private static void EnsureDefaultAgentInstance(PuddingDataPaths paths)
    {
        var instanceId = "default.general-assistant-001";
        var manifestPath = Path.Combine(paths.AgentInstanceRoot(instanceId), "manifest.json");
        if (File.Exists(manifestPath))
        {
            EnsureAgentSkillDirectory(paths, instanceId);
            return;
        }

        Serilog.Log.Information("[Bootstrap] 创建默认 Agent 实例: {InstanceId}", instanceId);

        var manifestDir = Path.GetDirectoryName(manifestPath)!;
        Directory.CreateDirectory(manifestDir);
        var manifest = """
        {
          "agentInstanceId": "default.general-assistant-001",
          "templateId": "general-assistant",
          "displayName": "布丁",
          "workspaceId": "default",
          "preferredProviderId": "deepseek",
          "preferredModelId": "deepseek-v4-pro",
          "isEnabled": true
        }
        """;
        File.WriteAllText(manifestPath, manifest);

        var configDir = paths.AgentInstanceConfigRoot(instanceId);
        Directory.CreateDirectory(configDir);
        var llmConfig = """
        {
          "conscious": {
            "providerId": "deepseek",
            "modelId": "deepseek-v4-pro"
          },
          "subconscious": {
            "providerId": "deepseek",
            "modelId": "deepseek-v4-flash"
          }
        }
        """;
        File.WriteAllText(Path.Combine(configDir, "llm.json"), llmConfig);

        var memoryConfig = """
        {
          "maxFacts": 1000,
          "maxPreferences": 200,
          "recallMode": "auto"
        }
        """;
        File.WriteAllText(Path.Combine(configDir, "memory.json"), memoryConfig);

        EnsureAgentSkillDirectory(paths, instanceId);
    }

    private static void EnsureAgentSkillDirectory(PuddingDataPaths paths, string agentInstanceId)
    {
        var skillsRoot = Path.Combine(paths.AgentInstanceRoot(agentInstanceId), "skills");
        Directory.CreateDirectory(skillsRoot);

        var indexPath = Path.Combine(skillsRoot, "index.json");
        if (File.Exists(indexPath))
            return;

        var index = $$"""
        {
          "agentInstanceId": "{{agentInstanceId}}",
          "generatedAt": "{{DateTimeOffset.UtcNow:O}}",
          "skills": []
        }
        """;
        File.WriteAllText(indexPath, index);
    }
}
