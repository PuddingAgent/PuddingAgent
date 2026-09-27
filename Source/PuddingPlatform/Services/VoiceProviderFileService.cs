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

    /// <summary>
    /// 有效默认项：运行时（VoiceProviderFactory / VoiceSynthesisService / AudioTranscriptionService）
    /// 只读根上的 Default{Tts,Asr}{Provider,Model}Id，因此模型的 IsDefault 必须与它保持一致，
    /// 否则设置页上的“设为默认”就是不生效的开关。
    /// </summary>
    public async Task<VoiceDefaultsDto> GetDefaultsAsync(CancellationToken ct = default)
    {
        var config = await LoadAsync(ct);
        return new VoiceDefaultsDto(
            config.DefaultTtsProviderId, config.DefaultTtsModelId,
            config.DefaultAsrProviderId, config.DefaultAsrModelId);
    }

    /// <summary>两个列表各自最多一个默认项，并同步根指针；TTS 与 ASR 互不影响。</summary>
    private static PuddingVoiceProvidersConfig ApplyTtsDefault(
        PuddingVoiceProvidersConfig config, PuddingVoiceProviderConfig owner, string modelId, bool isDefault)
    {
        foreach (var provider in config.Providers)
        {
            for (var index = 0; index < provider.TtsModels.Count; index++)
            {
                var current = provider.TtsModels[index];
                var shouldBeDefault = isDefault
                    && ReferenceEquals(provider, owner)
                    && string.Equals(current.ModelId, modelId, StringComparison.OrdinalIgnoreCase);
                if (current.IsDefault != shouldBeDefault)
                    provider.TtsModels[index] = current with { IsDefault = shouldBeDefault };
            }
        }
        if (isDefault) return config with { DefaultTtsProviderId = owner.ProviderId, DefaultTtsModelId = modelId };
        return OwnsDefault(config.DefaultTtsProviderId, config.DefaultTtsModelId, owner.ProviderId, modelId)
            ? config with { DefaultTtsProviderId = null, DefaultTtsModelId = null }
            : config;
    }

    private static PuddingVoiceProvidersConfig ApplyAsrDefault(
        PuddingVoiceProvidersConfig config, PuddingVoiceProviderConfig owner, string modelId, bool isDefault)
    {
        foreach (var provider in config.Providers)
        {
            for (var index = 0; index < provider.AsrModels.Count; index++)
            {
                var current = provider.AsrModels[index];
                var shouldBeDefault = isDefault
                    && ReferenceEquals(provider, owner)
                    && string.Equals(current.ModelId, modelId, StringComparison.OrdinalIgnoreCase);
                if (current.IsDefault != shouldBeDefault)
                    provider.AsrModels[index] = current with { IsDefault = shouldBeDefault };
            }
        }
        if (isDefault) return config with { DefaultAsrProviderId = owner.ProviderId, DefaultAsrModelId = modelId };
        return OwnsDefault(config.DefaultAsrProviderId, config.DefaultAsrModelId, owner.ProviderId, modelId)
            ? config with { DefaultAsrProviderId = null, DefaultAsrModelId = null }
            : config;
    }

    private static bool OwnsDefault(string? defaultProviderId, string? defaultModelId, string providerId, string modelId)
        => string.Equals(defaultProviderId, providerId, StringComparison.OrdinalIgnoreCase)
           && string.Equals(defaultModelId, modelId, StringComparison.OrdinalIgnoreCase);

    private static void Validate(UpsertVoiceProviderRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ProviderId) || request.ProviderId.Length > 80
            || request.ProviderId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_'))
            throw new ArgumentException("语音服务商 ID 只能包含字母、数字、'-' 和 '_'，且不超过 80 个字符。");
        if (string.IsNullOrWhiteSpace(request.Name)) throw new ArgumentException("语音服务商名称不能为空。");
        if (!Uri.TryCreate(request.Endpoint, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new ArgumentException("Endpoint 必须是 http/https 绝对地址，且不能带账号、查询或片段。");
        if (request.ClearApiKey && !string.IsNullOrWhiteSpace(request.ApiKey))
            throw new ArgumentException("不能同时替换和清除密钥。");
    }

    private static void ValidateModelId(string modelId, string kind)
    {
        if (string.IsNullOrWhiteSpace(modelId) || modelId.Length > 128)
            throw new ArgumentException($"{kind} 模型 ID 不能为空且不超过 128 个字符。");
    }

    private static void ValidateSampleRates(IReadOnlyList<int>? sampleRates, string kind, string modelId)
    {
        if (sampleRates is null) return;
        if (sampleRates.Any(rate => rate <= 0))
            throw new ArgumentException($"{kind} 模型 '{modelId}' 的采样率必须大于 0。");
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
        Validate(req);
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
                ApiKey = req.ClearApiKey ? "" : req.ApiKey ?? "",
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
        Validate(req);
        await _writeLock.WaitAsync(ct);
        try
        {
            var config = await LoadAsync(ct);
            var p = config.Providers.FirstOrDefault(x =>
                string.Equals(x.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
            if (p is null) throw new KeyNotFoundException($"Voice Provider '{providerId}' 不存在");

            // 更新字段（保留 models 不变）；ApiKey 为 null 表示保持，ClearApiKey 才是清除。
            var updated = p with
            {
                Name = req.Name,
                Endpoint = req.Endpoint,
                ApiKey = req.ClearApiKey ? "" : req.ApiKey ?? p.ApiKey,
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
            // 删除拥有默认项的 Provider 时同步清空根指针，避免默认项指向不存在的模型。
            if (string.Equals(config.DefaultTtsProviderId, p.ProviderId, StringComparison.OrdinalIgnoreCase))
                config = config with { DefaultTtsProviderId = null, DefaultTtsModelId = null };
            if (string.Equals(config.DefaultAsrProviderId, p.ProviderId, StringComparison.OrdinalIgnoreCase))
                config = config with { DefaultAsrProviderId = null, DefaultAsrModelId = null };
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
        ValidateModelId(req.ModelId, "TTS");
        ValidateSampleRates(req.SampleRates, "TTS", req.ModelId);
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
                IsDefault = false, // 由 ApplyTtsDefault 统一决定，保证两个列表各自只有一个默认项
                SortOrder = req.SortOrder,
            };

            p.TtsModels.Add(model);
            config = ApplyTtsDefault(config, p, req.ModelId, req.IsDefault);
            await SaveConfigAsync(config, ct);

            return MapTtsDto(p.TtsModels.First(m => string.Equals(m.ModelId, req.ModelId, StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<TtsModelDto> UpdateTtsModelAsync(string providerId, string modelId, UpsertTtsModelRequest req, CancellationToken ct = default)
    {
        ValidateSampleRates(req.SampleRates, "TTS", modelId);
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
                IsDefault = m.IsDefault,
                SortOrder = req.SortOrder,
            };

            var idx = p.TtsModels.IndexOf(m);
            p.TtsModels[idx] = updated;
            config = ApplyTtsDefault(config, p, modelId, req.IsDefault);
            await SaveConfigAsync(config, ct);

            return MapTtsDto(p.TtsModels[idx]);
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
            if (OwnsDefault(config.DefaultTtsProviderId, config.DefaultTtsModelId, p.ProviderId, modelId))
                config = config with { DefaultTtsProviderId = null, DefaultTtsModelId = null };
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
        ValidateModelId(req.ModelId, "ASR");
        ValidateSampleRates(req.SampleRates, "ASR", req.ModelId);
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
                IsDefault = false, // 由 ApplyAsrDefault 统一决定；与 TTS 默认项互不影响
                SortOrder = req.SortOrder,
            };

            p.AsrModels.Add(model);
            config = ApplyAsrDefault(config, p, req.ModelId, req.IsDefault);
            await SaveConfigAsync(config, ct);

            return MapAsrDto(p.AsrModels.First(m => string.Equals(m.ModelId, req.ModelId, StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<AsrModelDto> UpdateAsrModelAsync(string providerId, string modelId, UpsertAsrModelRequest req, CancellationToken ct = default)
    {
        ValidateSampleRates(req.SampleRates, "ASR", modelId);
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
                IsDefault = m.IsDefault,
                SortOrder = req.SortOrder,
            };

            var idx = p.AsrModels.IndexOf(m);
            p.AsrModels[idx] = updated;
            config = ApplyAsrDefault(config, p, modelId, req.IsDefault);
            await SaveConfigAsync(config, ct);

            return MapAsrDto(p.AsrModels[idx]);
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
            if (OwnsDefault(config.DefaultAsrProviderId, config.DefaultAsrModelId, p.ProviderId, modelId))
                config = config with { DefaultAsrProviderId = null, DefaultAsrModelId = null };
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
