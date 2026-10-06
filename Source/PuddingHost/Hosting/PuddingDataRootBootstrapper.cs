using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using PuddingCode.Configuration;

namespace PuddingHost.Hosting;

/// <summary>
/// Resolves and prepares the data root directory:
///   - Parse --data-root from args or PUDDING_DATA_ROOT env var
///   - Copy missing default-data files
///   - Create runtime directories
///   - Ensure a login-JWT signing key exists in config/security.json
///   - Create default Agent instance if missing
/// </summary>
public static class PuddingDataRootBootstrapper
{
    /// <summary>已随包发布的占位符密钥（旧模板值）：读取到即视为"未配置"，由引导期重新生成。</summary>
    private static readonly string[] KnownPlaceholderJwtKeys =
    [
        "local-dev-key-change-me-32plus",
        "Pudding-Platform-JWT-DevKey-MUST-CHANGE-IN-PRODUCTION-32PLUS!",
    ];

    /// <summary>持久化密钥的最小长度：短于此值视为占位/误配并重新生成（HS256 硬要求仅 16）。</summary>
    private const int MinimumPersistedJwtKeyLength = 32;

    /// <summary>随机密钥字节数（Base64 后 64 字符）。</summary>
    private const int JwtKeyByteCount = 48;

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
        EnsureSecurityJwtKey(dataPaths);
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

    /// <summary>
    /// 确保 <c>config/security.json</c> 中存在可用的登录态 JWT 签名密钥。
    /// <para>
    /// 背景：JWT 密钥已不再有代码内硬编码兜底（缺失即启动失败），但随包发布的模板不可能携带真实密钥。
    /// 因此引导期在这里做一次性配置：文件缺失则按模板创建；<c>jwt.key</c> 缺失、过短或等于随包占位符时，
    /// 生成 48 字节 CSPRNG 密钥并原子写回（其余字段原样保留）。密钥内容不写日志。
    /// </para>
    /// </summary>
    private static void EnsureSecurityJwtKey(PuddingDataPaths paths)
    {
        var path = paths.SystemConfigFile("security.json");
        var root = ReadSecurityRoot(path);

        if (root["jwt"] is not JsonObject jwt)
        {
            jwt = new JsonObject();
            root["jwt"] = jwt;
        }

        var currentKey = (jwt["key"]?.GetValue<string>() ?? string.Empty).Trim();
        var isUsable = currentKey.Length >= MinimumPersistedJwtKeyLength
                       && !KnownPlaceholderJwtKeys.Contains(currentKey, StringComparer.Ordinal);
        if (isUsable)
            return;

        var generatedKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(JwtKeyByteCount));
        jwt["key"] = generatedKey;

        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tempPath, path, overwrite: true);

        Serilog.Log.Information(
            "[Bootstrap] 已在 {Path} 生成新的登录态 JWT 签名密钥（长度 {Length}，内容不入日志）",
            path,
            generatedKey.Length);
    }

    /// <summary>读取 security.json 根对象；文件缺失时返回模板结构；非法 JSON 直接失败（fail closed）。</summary>
    private static JsonObject ReadSecurityRoot(string path)
    {
        if (!File.Exists(path))
        {
            return new JsonObject
            {
                ["jwt"] = new JsonObject
                {
                    ["issuer"] = "pudding-platform",
                    ["audience"] = "pudding-admin",
                    // 与 PuddingJwtSettings.DefaultExpiryHours 一致：7 天。
                    ["expiryHours"] = 168,
                    ["key"] = string.Empty,
                },
                ["keyVault"] = new JsonObject
                {
                    ["mode"] = "local-file",
                    ["masterKeyRef"] = "local",
                },
            };
        }

        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                   ?? throw new InvalidOperationException($"根节点必须是 JSON 对象：{path}");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"无法解析 {path}（登录态 JWT 密钥所在文件）：{ex.Message}",
                ex);
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
          "preferredModelId": "deepseek-flash",
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
            "modelId": "deepseek-flash"
          },
          "subconscious": {
            "providerId": "deepseek",
            "modelId": "deepseek-flash"
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
