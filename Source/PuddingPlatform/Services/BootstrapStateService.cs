using System.Text.Json.Nodes;

namespace PuddingPlatform.Services;

/// <summary>
/// 管理 bootstrap-state.json 的读写，封装初始化锁定与密钥持久化。
/// </summary>
public class BootstrapStateService
{
    private readonly string _filePath;
    private readonly IConfiguration _config;

    public bool IsInitialized => _config.GetValue<bool>("Bootstrap:Initialized");
    public string Secret => _config["Bootstrap:Secret"] ?? string.Empty;

    public BootstrapStateService(string filePath, IConfiguration config)
    {
        _filePath = filePath;
        _config = config;
    }

    /// <summary>将 Bootstrap:Initialized 置为 true 并以排他锁写回文件。</summary>
    public async Task SetInitializedAsync()
    {
        var json = await File.ReadAllTextAsync(_filePath);
        var node = JsonNode.Parse(json)!;
        node["Bootstrap"]!["Initialized"] = true;
        var updatedJson = node.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = false });

        await using var fs = new FileStream(_filePath, FileMode.Create, FileAccess.Write, FileShare.None);
        await using var sw = new StreamWriter(fs);
        await sw.WriteAsync(updatedJson);
    }
}
