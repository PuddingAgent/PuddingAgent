using System.Text.Json;
using PuddingCode.Configuration;
using PuddingPlatform.Data.Dtos;

namespace PuddingPlatform.Services;

/// <summary>
/// 文件式 TTS/ASR Provider/Model 管理服务 — 读写 data/config/voice/providers.json。
/// 与 LLM 资源池完全独立，不依赖 llm/providers.json。
/// </summary>
public sealed class VoiceProviderFileService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly PuddingDataPaths _paths;
    private readonly ILogger<VoiceProviderFileService> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public VoiceProviderFileService(PuddingDataPaths paths, ILogger<VoiceProviderFileService> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    private string ConfigPath => _paths.SystemConfigFile("voice/providers.json");

    public async Task<PuddingVoiceProvidersConfig> LoadAsync(CancellationToken ct = default)
    {
        var config = await AtomicFileWriter.ReadJsonAsync<PuddingVoiceProvidersConfig>(ConfigPath, JsonOptions, ct);
        return config ?? new PuddingVoiceProvidersConfig();
    }

    private async Task SaveConfigAsync(PuddingVoiceProvidersConfig config, CancellationToken ct)
    {
        await AtomicFileWriter.WriteJsonAsync(ConfigPath, config, JsonOptions, ct);
    }

    // ── Provider CRUD ──────────────────────────────────────────

    public async Task<List<VoiceProviderDto>> ListProvidersAsync(CancellationToken ct = default)
    {
        var config = await LoadAsync(ct);
        return config.Providers.Select(p => new VoiceProviderDto(
            ProviderId: p.ProviderId,
            Name: p.Name,
            Endpoint: p.Endpoint,
            HasApiKey: !string.IsNullOrWhiteSpace(p.ApiKey),
            Description: p.Description,
            IsEnabled: p.IsEnabled,
            TtsModelCount: p.TtsModels.Count,
            AsrModelCount: p.AsrModels.Count,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow
        )).ToList();
    }

    public async Task<VoiceProviderDetailDto?> GetProviderAsync(string providerId, CancellationToken ct = default)
    {
        var config = await LoadAsync(ct);
        var p = config.Providers.FirstOrDefault(x =>
            string.Equals(x.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
        if (p is null) return null;

        return new VoiceProviderDetailDto(
            ProviderId: p.ProviderId,
            Name: p.Name,
            Endpoint: p.Endpoint,
            HasApiKey: !string.IsNullOrWhiteSpace(p.ApiKey),
            Description: p.Description,
            IsEnabled: p.IsEnabled,
            TtsModels: p.TtsModels.Select(m => new TtsModelDto(
                ModelId: m.ModelId,
                Name: m.Name,
                Path: m.Path,
                Voices: m.Voices,
                AudioFormats: m.AudioFormats,
                SampleRates: m.SampleRates,
                SupportsStreaming: m.SupportsStreaming,
                SupportsInstructions: m.SupportsInstructions,
                SupportsVoiceCloning: m.SupportsVoiceCloning,
                SupportsVoiceDesign: m.SupportsVoiceDesign,
                IsDeprecated: m.IsDeprecated,
                IsDefault: m.IsDefault,
                SortOrder: m.SortOrder
            )).ToList(),
            AsrModels: p.AsrModels.Select(m => new AsrModelDto(
                ModelId: m.ModelId,
                Name: m.Name,
                Path: m.Path,
                Languages: m.Languages,
                SampleRates: m.SampleRates,
                SupportsEmotion: m.SupportsEmotion,
                SupportsTimestamps: m.SupportsTimestamps,
                SupportsHotWords: m.SupportsHotWords,
                IsDeprecated: m.IsDeprecated,
                IsDefault: m.IsDefault,
                SortOrder: m.SortOrder
            )).ToList(),
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow
        );
    }

    public async Task<VoiceProviderDto> CreateProviderAsync(UpsertVoiceProviderRequest req, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var config = await LoadAsync(ct);

            if (config.Providers.Any(p => string.Equals(p.ProviderId, req.ProviderId, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Voice ProviderId '{req.ProviderId}' 已存在");

            var newProvider = new PuddingVoiceProviderConfig
            {
                ProviderId = req.ProviderId,
                Name = req.Name,
                Endpoint = req.Endpoint,
                ApiKey = req.ApiKey ?? "",
                Description = req.Description,
                IsEnabled = req.IsEnabled,
            };

            config.Providers.Add(newProvider);
            await SaveConfigAsync(config, ct);

            return new VoiceProviderDto(
                ProviderId: newProvider.ProviderId,
                Name: newProvider.Name,
                Endpoint: newProvider.Endpoint,
                HasApiKey: !string.IsNullOrWhiteSpace(newProvider.ApiKey),
                Description: newProvider.Description,
                IsEnabled: newProvider.IsEnabled,
                TtsModelCount: 0,
                AsrModelCount: 0,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow
            );
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<VoiceProviderDto> UpdateProviderAsync(string providerId, UpsertVoiceProviderRequest req, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var config = await LoadAsync(ct);
            var p = config.Providers.FirstOrDefault(x =>
                string.Equals(x.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
            if (p is null) throw new KeyNotFoundException($"Voice Provider '{providerId}' 不存在");

            // 更新字段（保留 models 不变）
            var updated = p with
            {
                Name = req.Name,
                Endpoint = req.Endpoint,
                ApiKey = req.ApiKey ?? p.ApiKey,
                Description = req.Description,
                IsEnabled = req.IsEnabled,
            };

            var idx = config.Providers.IndexOf(p);
            config.Providers[idx] = updated;
            await SaveConfigAsync(config, ct);

            return new VoiceProviderDto(
                ProviderId: updated.ProviderId,
                Name: updated.Name,
                Endpoint: updated.Endpoint,
                HasApiKey: !string.IsNullOrWhiteSpace(updated.ApiKey),
                Description: updated.Description,
                IsEnabled: updated.IsEnabled,
                TtsModelCount: updated.TtsModels.Count,
                AsrModelCount: updated.AsrModels.Count,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow
            );
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task DeleteProviderAsync(string providerId, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var config = await LoadAsync(ct);
            var p = config.Providers.FirstOrDefault(x =>
                string.Equals(x.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
            if (p is null) throw new KeyNotFoundException($"Voice Provider '{providerId}' 不存在");

            config.Providers.Remove(p);
            await SaveConfigAsync(config, ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ── TTS Model CRUD ─────────────────────────────────────────

    public async Task<TtsModelDto> CreateTtsModelAsync(string providerId, UpsertTtsModelRequest req, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var config = await LoadAsync(ct);
            var p = config.Providers.FirstOrDefault(x =>
                string.Equals(x.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
            if (p is null) throw new KeyNotFoundException($"Voice Provider '{providerId}' 不存在");

            if (p.TtsModels.Any(m => string.Equals(m.ModelId, req.ModelId, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"TTS Model '{req.ModelId}' 已存在");

            var model = new PuddingTtsModelConfig
            {
                ModelId = req.ModelId,
                Name = req.Name,
                Path = req.Path,
                Voices = req.Voices ?? [],
                AudioFormats = req.AudioFormats ?? [],
                SampleRates = req.SampleRates ?? [],
                SupportsStreaming = req.SupportsStreaming,
                SupportsInstructions = req.SupportsInstructions,
                SupportsVoiceCloning = req.SupportsVoiceCloning,
                SupportsVoiceDesign = req.SupportsVoiceDesign,
                IsDeprecated = req.IsDeprecated,
                IsDefault = req.IsDefault,
                SortOrder = req.SortOrder,
            };

            p.TtsModels.Add(model);
            await SaveConfigAsync(config, ct);

            return MapTtsDto(model);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<TtsModelDto> UpdateTtsModelAsync(string providerId, string modelId, UpsertTtsModelRequest req, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var config = await LoadAsync(ct);
            var p = config.Providers.FirstOrDefault(x =>
                string.Equals(x.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
            if (p is null) throw new KeyNotFoundException($"Voice Provider '{providerId}' 不存在");

            var m = p.TtsModels.FirstOrDefault(x =>
                string.Equals(x.ModelId, modelId, StringComparison.OrdinalIgnoreCase));
            if (m is null) throw new KeyNotFoundException($"TTS Model '{modelId}' 不存在");

            var updated = m with
            {
                Name = req.Name,
                Path = req.Path,
                Voices = req.Voices ?? m.Voices,
                AudioFormats = req.AudioFormats ?? m.AudioFormats,
                SampleRates = req.SampleRates ?? m.SampleRates,
                SupportsStreaming = req.SupportsStreaming,
                SupportsInstructions = req.SupportsInstructions,
                SupportsVoiceCloning = req.SupportsVoiceCloning,
                SupportsVoiceDesign = req.SupportsVoiceDesign,
                IsDeprecated = req.IsDeprecated,
                IsDefault = req.IsDefault,
                SortOrder = req.SortOrder,
            };

            var idx = p.TtsModels.IndexOf(m);
            p.TtsModels[idx] = updated;
            await SaveConfigAsync(config, ct);

            return MapTtsDto(updated);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task DeleteTtsModelAsync(string providerId, string modelId, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var config = await LoadAsync(ct);
            var p = config.Providers.FirstOrDefault(x =>
                string.Equals(x.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
            if (p is null) throw new KeyNotFoundException($"Voice Provider '{providerId}' 不存在");

            var m = p.TtsModels.FirstOrDefault(x =>
                string.Equals(x.ModelId, modelId, StringComparison.OrdinalIgnoreCase));
            if (m is null) throw new KeyNotFoundException($"TTS Model '{modelId}' 不存在");

            p.TtsModels.Remove(m);
            await SaveConfigAsync(config, ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ── ASR Model CRUD ─────────────────────────────────────────

    public async Task<AsrModelDto> CreateAsrModelAsync(string providerId, UpsertAsrModelRequest req, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var config = await LoadAsync(ct);
            var p = config.Providers.FirstOrDefault(x =>
                string.Equals(x.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
            if (p is null) throw new KeyNotFoundException($"Voice Provider '{providerId}' 不存在");

            if (p.AsrModels.Any(m => string.Equals(m.ModelId, req.ModelId, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"ASR Model '{req.ModelId}' 已存在");

            var model = new PuddingAsrModelConfig
            {
                ModelId = req.ModelId,
                Name = req.Name,
                Path = req.Path,
                Languages = req.Languages ?? [],
                SampleRates = req.SampleRates ?? [],
                SupportsEmotion = req.SupportsEmotion,
                SupportsTimestamps = req.SupportsTimestamps,
                SupportsHotWords = req.SupportsHotWords,
                IsDeprecated = req.IsDeprecated,
                IsDefault = req.IsDefault,
                SortOrder = req.SortOrder,
            };

            p.AsrModels.Add(model);
            await SaveConfigAsync(config, ct);

            return MapAsrDto(model);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<AsrModelDto> UpdateAsrModelAsync(string providerId, string modelId, UpsertAsrModelRequest req, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var config = await LoadAsync(ct);
            var p = config.Providers.FirstOrDefault(x =>
                string.Equals(x.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
            if (p is null) throw new KeyNotFoundException($"Voice Provider '{providerId}' 不存在");

            var m = p.AsrModels.FirstOrDefault(x =>
                string.Equals(x.ModelId, modelId, StringComparison.OrdinalIgnoreCase));
            if (m is null) throw new KeyNotFoundException($"ASR Model '{modelId}' 不存在");

            var updated = m with
            {
                Name = req.Name,
                Path = req.Path,
                Languages = req.Languages ?? m.Languages,
                SampleRates = req.SampleRates ?? m.SampleRates,
                SupportsEmotion = req.SupportsEmotion,
                SupportsTimestamps = req.SupportsTimestamps,
                SupportsHotWords = req.SupportsHotWords,
                IsDeprecated = req.IsDeprecated,
                IsDefault = req.IsDefault,
                SortOrder = req.SortOrder,
            };

            var idx = p.AsrModels.IndexOf(m);
            p.AsrModels[idx] = updated;
            await SaveConfigAsync(config, ct);

            return MapAsrDto(updated);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task DeleteAsrModelAsync(string providerId, string modelId, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var config = await LoadAsync(ct);
            var p = config.Providers.FirstOrDefault(x =>
                string.Equals(x.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
            if (p is null) throw new KeyNotFoundException($"Voice Provider '{providerId}' 不存在");

            var m = p.AsrModels.FirstOrDefault(x =>
                string.Equals(x.ModelId, modelId, StringComparison.OrdinalIgnoreCase));
            if (m is null) throw new KeyNotFoundException($"ASR Model '{modelId}' 不存在");

            p.AsrModels.Remove(m);
            await SaveConfigAsync(config, ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ── Helpers ────────────────────────────────────────────────

    private static TtsModelDto MapTtsDto(PuddingTtsModelConfig m) => new(
        ModelId: m.ModelId,
        Name: m.Name,
        Path: m.Path,
        Voices: m.Voices,
        AudioFormats: m.AudioFormats,
        SampleRates: m.SampleRates,
        SupportsStreaming: m.SupportsStreaming,
        SupportsInstructions: m.SupportsInstructions,
        SupportsVoiceCloning: m.SupportsVoiceCloning,
        SupportsVoiceDesign: m.SupportsVoiceDesign,
        IsDeprecated: m.IsDeprecated,
        IsDefault: m.IsDefault,
        SortOrder: m.SortOrder
    );

    private static AsrModelDto MapAsrDto(PuddingAsrModelConfig m) => new(
        ModelId: m.ModelId,
        Name: m.Name,
        Path: m.Path,
        Languages: m.Languages,
        SampleRates: m.SampleRates,
        SupportsEmotion: m.SupportsEmotion,
        SupportsTimestamps: m.SupportsTimestamps,
        SupportsHotWords: m.SupportsHotWords,
        IsDeprecated: m.IsDeprecated,
        IsDefault: m.IsDefault,
        SortOrder: m.SortOrder
    );
}
