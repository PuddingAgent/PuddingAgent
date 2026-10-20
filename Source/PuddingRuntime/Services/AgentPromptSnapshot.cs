using PuddingCode.Agents;
using PuddingCode.Configuration;
using System.Text;
using System.Text.Json;

namespace PuddingRuntime.Services;

/// <summary>编辑页与推理共同使用实例 manifest 的文档引用；空正文不回退到模板。</summary>
internal sealed record AgentPromptSnapshot(AgentInstanceManifest? Manifest, AgentPersonaFiles? Persona)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
    };
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static async Task<AgentPromptSnapshot> LoadAsync(PuddingDataPaths? paths, string? agentId, CancellationToken ct)
    {
        if (paths is null || string.IsNullOrWhiteSpace(agentId)) return new(null, null);
        var root = paths.AgentInstanceRoot(agentId);
        if (!Directory.Exists(root)) return new(null, null);
        var manifest = await AtomicFileWriter.ReadJsonAsync<AgentInstanceManifest>(Path.Combine(root, "manifest.json"), JsonOptions, ct);

        async Task<string?> ReadAsync(string? reference, string fileName)
        {
            // 已创建实例只消费自身快照。缺失/清空文档不得引入来源模板的新指令。
            if (manifest is not null && string.IsNullOrWhiteSpace(reference)) return "";
            var path = Path.Combine(root, reference ?? fileName);
            return File.Exists(path) ? await File.ReadAllTextAsync(path, Utf8, ct) : manifest is null ? null : "";
        }

        return new(manifest, new AgentPersonaFiles
        {
            Soul = await ReadAsync(manifest?.SoulMdFile, "SOUL.md"),
            Agents = await ReadAsync(manifest?.AgentsMdFile, "AGENTS.md"),
            Tools = await ReadAsync(manifest?.ToolsMdFile, "TOOLS.md"),
            Bootstrap = await ReadAsync(manifest?.BootstrapMdFile, "BOOTSTRAP.md"),
            Memory = await ReadAsync(manifest?.MemoryMdFile, "MEMORY.md"),
            Identity = await ReadAsync("IDENTITY.md", "IDENTITY.md"),
            User = await ReadAsync("USER.md", "USER.md"),
        });
    }
}
