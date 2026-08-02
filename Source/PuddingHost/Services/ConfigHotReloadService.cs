using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingPlatform.Services;

namespace PuddingAgent.Services;

/// <summary>
/// 配置文件热重载服务 — 监听 data/config/ 等关键目录的文件变更，
/// 自动触发 LLM 配置缓存失效，使 Agent 修改配置后无需重启即可生效。
/// </summary>
public sealed class ConfigHotReloadService : BackgroundService
{
    private readonly ILlmConfigService _llmConfig;
    private readonly LlmProviderFileService _providerFileService;
    private readonly ILogger<ConfigHotReloadService> _logger;
    private readonly string _configDir;

    public ConfigHotReloadService(
        ILlmConfigService llmConfig,
        LlmProviderFileService providerFileService,
        ILogger<ConfigHotReloadService> logger)
    {
        _llmConfig = llmConfig;
        _providerFileService = providerFileService;
        _logger = logger;
        _configDir = Path.Combine(
            Environment.GetEnvironmentVariable("PUDDING_DATA_ROOT")
            ?? Path.Combine(AppContext.BaseDirectory, "data"),
            "config");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Directory.Exists(_configDir))
        {
            _logger.LogWarning("[HotReload] 配置目录不存在，跳过文件监听: {Dir}", _configDir);
            return;
        }

        _logger.LogInformation("[HotReload] 开始监听配置目录变更: {Dir}", _configDir);

        using var watcher = new FileSystemWatcher(_configDir)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            Filter = "*.json",
            EnableRaisingEvents = true,
        };

        // 防抖：300ms 内多次变更只触发一次重载
        var debounceCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var debounceLock = new object();

        watcher.Changed += OnConfigFileChanged;
        watcher.Created += OnConfigFileChanged;
        watcher.Deleted += OnConfigFileChanged;
        watcher.Renamed += (_, e) =>
        {
            if (e.FullPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                TriggerReload("rename", e.FullPath);
        };

        // 阻塞直到服务停止
        await Task.Delay(Timeout.Infinite, stoppingToken);

        void OnConfigFileChanged(object sender, FileSystemEventArgs e)
        {
            // 忽略临时文件
            if (e.Name?.StartsWith(".tmp", StringComparison.Ordinal) == true
                || e.Name?.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) == true)
                return;

            TriggerReload(e.ChangeType.ToString().ToLowerInvariant(), e.FullPath);
        }

        void TriggerReload(string changeType, string path)
        {
            lock (debounceLock)
            {
                debounceCts.Cancel();
                debounceCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            }

            var fileName = Path.GetFileName(path);
            var delay = Task.Delay(300, debounceCts.Token);

            _ = delay.ContinueWith(_ =>
            {
                _logger.LogInformation("[HotReload] 检测到配置文件变动: {File} ({Change})", fileName, changeType);

                if (string.Equals(fileName, "llm.providers.json", StringComparison.OrdinalIgnoreCase))
                {
                    ReloadLlmConfig();
                }
                else if (path.Contains("agent-templates", StringComparison.OrdinalIgnoreCase))
                {
                    // 模板文件变更无需缓存失效 — AgentTemplateFileService 每次都从磁盘读取
                    _logger.LogInformation("[HotReload] Agent template config changed: {File}", fileName);
                }
                else
                {
                    _logger.LogDebug("[HotReload] Ignoring file change: {File}", fileName);
                }
            }, debounceCts.Token, TaskContinuationOptions.NotOnCanceled, TaskScheduler.Default);
        }
    }

    private void ReloadLlmConfig()
    {
        try
        {
            var newConfig = _providerFileService.LoadAsync(CancellationToken.None)
                .GetAwaiter().GetResult();
            _llmConfig.Reload(newConfig);
            _logger.LogInformation("[HotReload] LLM config reloaded successfully from disk");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[HotReload] Failed to reload LLM config");
        }
    }

    /// <summary>
    /// 手动触发所有配置重载（可通过 reload API 调用）。
    /// </summary>
    public void ReloadAll()
    {
        _logger.LogInformation("[HotReload] 手动触发全配置重载");
        ReloadLlmConfig();
    }
}
