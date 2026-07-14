using Microsoft.Extensions.Logging;

namespace PuddingCode.Agents;

/// <summary>
/// Agent Persona 文件读取器：从 data/agents/{templateId}/ 目录读取 MD 文件。
/// 按优先级：文件 &gt; DB Persona 字段 &gt; 内置模板兜底。
/// </summary>
public sealed class AgentPersonaFileProvider
{
    private readonly string _baseDir;
    private readonly ILogger<AgentPersonaFileProvider> _logger;

    public AgentPersonaFileProvider(string baseDir, ILogger<AgentPersonaFileProvider> logger)
    {
        _baseDir = baseDir;
        _logger = logger;
    }

    /// <summary>
    /// 读取 Agent 目录下所有 Persona 文件内容。
    /// </summary>
    public AgentPersonaFiles? Load(string templateId)
    {
        // 规范化 templateId：去除 "global:" 前缀
        var cleanId = templateId.StartsWith("global:")
            ? templateId["global:".Length..]
            : templateId;
        var dir = Path.Combine(_baseDir, cleanId);

        if (!Directory.Exists(dir))
        {
            _logger.LogDebug("[PersonaFile] Directory not found for template={Template}", templateId);
            return null;
        }

        return new AgentPersonaFiles
        {
            Soul        = ReadIfExists(dir, "SOUL.md"),
            Agents      = ReadIfExists(dir, "AGENTS.md"),
            Tools       = ReadIfExists(dir, "TOOLS.md"),
            Bootstrap   = ReadIfExists(dir, "BOOTSTRAP.md"),
            Identity    = ReadIfExists(dir, "IDENTITY.md"),
            User        = ReadIfExists(dir, "USER.md"),
        };
    }

        /// <summary>
    /// 读取 Persona 文件，优先实例级（data/agents/{agentInstanceId}/persona/），
    /// 回退模板级（data/agents/{templateId}/）。
    /// </summary>
    public AgentPersonaFiles? Load(string templateId, string? agentInstanceId)
    {
        // 1. 先读实例级 persona 目录
        AgentPersonaFiles? instanceFiles = null;
        if (!string.IsNullOrWhiteSpace(agentInstanceId))
        {
            var instanceDir = Path.Combine(_baseDir, agentInstanceId, "persona");
            if (Directory.Exists(instanceDir))
            {
                instanceFiles = new AgentPersonaFiles
                {
                    Soul = ReadIfExists(instanceDir, "SOUL.md"),
                    Agents = ReadIfExists(instanceDir, "AGENTS.md"),
                    Tools = ReadIfExists(instanceDir, "TOOLS.md"),
                    Bootstrap = ReadIfExists(instanceDir, "BOOTSTRAP.md"),
                    Identity = ReadIfExists(instanceDir, "IDENTITY.md"),
                    User = ReadIfExists(instanceDir, "USER.md"),
                    Memory = ReadIfExists(instanceDir, "MEMORY.md"),
                };
            }
        }

        // 2. 读模板级作为基底
        var templateFiles = Load(templateId);

        // 3. 合并：实例级覆盖模板级
        return new AgentPersonaFiles
        {
            Soul = instanceFiles?.Soul ?? templateFiles?.Soul,
            Agents = instanceFiles?.Agents ?? templateFiles?.Agents,
            Tools = instanceFiles?.Tools ?? templateFiles?.Tools,
            Bootstrap = instanceFiles?.Bootstrap ?? templateFiles?.Bootstrap,
            Identity = instanceFiles?.Identity ?? templateFiles?.Identity,
            User = instanceFiles?.User ?? templateFiles?.User,
            Memory = instanceFiles?.Memory ?? templateFiles?.Memory,
        };
    }

    /// <summary>
    /// 写入 Agent 实例级 Persona 文件。
    /// 文件写入 data/agents/{agentInstanceId}/persona/{fileName}，不影响模板级文件。
    /// </summary>
    public void Write(string agentInstanceId, string fileName, string content)
    {
        var dir = Path.Combine(_baseDir, agentInstanceId, "persona");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        File.WriteAllText(path, content);
        _logger.LogInformation("[PersonaFile] Written instance={Agent} file={File} chars={Len}",
            agentInstanceId, fileName, content.Length);
    }

    /// <summary>
    /// 列出已安装的 Agent 模板 ID（有目录即视为已安装）。
    /// </summary>
    public IReadOnlyList<string> ListInstalled()
    {
        if (!Directory.Exists(_baseDir))
            return Array.Empty<string>();

        return Directory.GetDirectories(_baseDir)
            .Select(Path.GetFileName)
            .Where(d => d is not null)
            .Cast<string>()
            .ToList();
    }

    private static string? ReadIfExists(string dir, string fileName)
    {
        var path = Path.Combine(dir, fileName);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }
}

/// <summary>
/// Agent Persona 文件集合。
/// </summary>
public sealed record AgentPersonaFiles
{
    /// <summary>SOUL.md — 人设、语气、边界</summary>
    public string? Soul { get; init; }
    /// <summary>AGENTS.md — 操作规则</summary>
    public string? Agents { get; init; }
    /// <summary>TOOLS.md — 工具使用约定</summary>
    public string? Tools { get; init; }
    /// <summary>BOOTSTRAP.md — 首次对话引导</summary>
    public string? Bootstrap { get; init; }
    /// <summary>IDENTITY.md — 名称、头像、身份描述（可选）</summary>
    public string? Identity { get; init; }
    /// <summary>USER.md — 用户画像</summary>
    public string? User { get; init; }
    /// <summary>MEMORY.md — 记忆策略与索引</summary>
    public string? Memory { get; init; }

    /// <summary>是否有任何非空文件</summary>
    public bool IsEmpty =>
        Soul is null && Agents is null && Tools is null &&
        Bootstrap is null && Identity is null && User is null && Memory is null;
}
