using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PuddingDesktop.Foundation;

namespace PuddingDesktop;

/// <summary>
/// DS-03 native voice settings: TTS/ASR providers, TTS models and ASR models. Every call goes through
/// IVoiceResourceSettings into the in-process Core; validation, the write lock, atomic replacement and
/// default-pointer synchronisation stay in Core.
/// </summary>
public sealed partial class MainWindow
{
    private IReadOnlyList<VoiceProviderSummary> _voiceProviders = [];
    private IReadOnlyList<VoiceTtsModel> _voiceTts = [];
    private IReadOnlyList<VoiceAsrModel> _voiceAsr = [];
    private VoiceProviderSummary? _voiceProvider;
    private VoiceTtsModel? _voiceTtsModel;
    private VoiceAsrModel? _voiceAsrModel;
    private VoiceDefaults _voiceDefaults = VoiceDefaults.None;
    private bool _voiceBuilt;
    private bool _voiceLoading;

    private ComboBox _vpPicker = null!;
    private TextBox _vpId = null!, _vpName = null!, _vpEndpoint = null!, _vpDescription = null!;
    private ComboBox _vpKeyChange = null!;
    private PasswordBox _vpNewKey = null!;
    private ToggleSwitch _vpEnabled = null!;
    private TextBlock _vpKeyState = null!, _vpSummary = null!;
    private InfoBar _vpNotice = null!;
    private Button _vpSave = null!, _vpDelete = null!;

    private ComboBox _vtProvider = null!, _vtPicker = null!;
    private TextBox _vtId = null!, _vtName = null!, _vtPath = null!, _vtVoices = null!, _vtFormats = null!, _vtRates = null!, _vtSort = null!;
    private ToggleSwitch _vtStreaming = null!, _vtInstructions = null!, _vtCloning = null!, _vtDesign = null!, _vtDefault = null!, _vtDeprecated = null!;
    private TextBlock _vtDefaults = null!;
    private InfoBar _vtNotice = null!;
    private Button _vtSave = null!, _vtDelete = null!;

    private ComboBox _vaProvider = null!, _vaPicker = null!;
    private TextBox _vaId = null!, _vaName = null!, _vaPath = null!, _vaLanguages = null!, _vaRates = null!, _vaSort = null!;
    private ToggleSwitch _vaEmotion = null!, _vaTimestamps = null!, _vaHotWords = null!, _vaDefault = null!, _vaDeprecated = null!;
    private TextBlock _vaDefaults = null!;
    private InfoBar _vaNotice = null!;
    private Button _vaSave = null!, _vaDelete = null!;

    private void BuildVoicePanels()
    {
        if (_voiceBuilt) return;
        _voiceBuilt = true;

        _vpPicker = new ComboBox { Header = "语音服务商", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_vpPicker, "选择语音服务商");
        _vpPicker.SelectionChanged += (_, _) => ApplyVoiceProviderSelection();
        _vpId = Field("服务商 ID", "例如 dashscope");
        _vpName = Field("名称");
        _vpEndpoint = Field("Endpoint", "https://dashscope.aliyuncs.com");
        _vpDescription = Field("描述");
        _vpEnabled = new ToggleSwitch { Header = "启用该服务商", OnContent = "已启用", OffContent = "已停用", IsOn = true };
        _vpKeyChange = new ComboBox { Header = "API Key", HorizontalAlignment = HorizontalAlignment.Stretch };
        _vpKeyChange.Items.Add(new ComboBoxItem { Content = "保持现有密钥", Tag = ApiKeyChange.Keep });
        _vpKeyChange.Items.Add(new ComboBoxItem { Content = "替换为新密钥", Tag = ApiKeyChange.Replace });
        _vpKeyChange.Items.Add(new ComboBoxItem { Content = "清除已保存的密钥", Tag = ApiKeyChange.Clear });
        _vpKeyChange.SelectedIndex = 0;
        _vpKeyChange.SelectionChanged += (_, _) => UpdateVoiceKeyEditor();
        _vpNewKey = new PasswordBox { Header = "新密钥（仅在替换时写入）", Visibility = Visibility.Collapsed };
        AutomationProperties.SetName(_vpNewKey, "新的语音服务商密钥");
        _vpKeyState = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = .7, FontSize = 12 };
        _vpSummary = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = .7, FontSize = 12 };
        _vpNotice = new InfoBar { IsOpen = false, IsClosable = true };
        _vpDelete = new Button { Content = "删除服务商" };
        _vpDelete.Click += async (_, _) => await OnVoiceDeleteProviderAsync();
        _vpSave = new Button { Content = "保存服务商" };
        _vpSave.Click += async (_, _) => await SaveVoiceProviderAsync();
        var newProvider = new Button { Content = "新建服务商" };
        newProvider.Click += (_, _) => OnVoiceNewProvider();
        var refresh = new Button { Content = "刷新" };
        refresh.Click += async (_, _) => await LoadVoiceAsync();

        VoiceProvidersSettings.Content = new VoicePanel(
            "服务商连接", "语音服务商与 LLM 资源池完全独立（config/voice/providers.json）。密钥只显示是否已配置，不显示明文。",
            _vpPicker, Row(newProvider, _vpDelete, refresh),
            _vpId, _vpName, _vpEndpoint, _vpDescription, _vpEnabled,
            _vpKeyChange, _vpNewKey, _vpKeyState, _vpSummary, _vpSave, _vpNotice);

        _vtProvider = new ComboBox { Header = "服务商", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_vtProvider, "TTS 模型所属服务商");
        _vtProvider.SelectionChanged += (_, _) => { if (Selected(_vtProvider) is { } id) _ = LoadVoiceModelsAsync(id); };
        _vtPicker = new ComboBox { Header = "TTS 模型", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_vtPicker, "选择 TTS 模型");
        _vtPicker.SelectionChanged += (_, _) => ApplyVoiceTtsSelection();
        _vtId = Field("模型 ID", "例如 cosyvoice-v3-flash");
        _vtName = Field("模型名称");
        _vtPath = Field("Path", "/api/v1/services/audio/tts/SpeechSynthesizer");
        _vtVoices = Field("音色列表", "逗号分隔，例如 longanyang, longanhuan_v3");
        _vtFormats = Field("音频格式", "逗号分隔，例如 wav, mp3");
        _vtRates = Field("采样率", "逗号分隔，例如 24000, 48000");
        _vtSort = Field("排序", "0");
        _vtStreaming = new ToggleSwitch { Header = "流式合成", OnContent = "支持", OffContent = "不支持" };
        _vtInstructions = new ToggleSwitch { Header = "指令控制", OnContent = "支持", OffContent = "不支持" };
        _vtCloning = new ToggleSwitch { Header = "音色克隆", OnContent = "支持", OffContent = "不支持" };
        _vtDesign = new ToggleSwitch { Header = "音色设计", OnContent = "支持", OffContent = "不支持" };
        _vtDefault = new ToggleSwitch { Header = "设为默认 TTS 模型", OnContent = "默认", OffContent = "非默认" };
        _vtDeprecated = new ToggleSwitch { Header = "已废弃", OnContent = "已废弃", OffContent = "可用" };
        _vtDefaults = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = .7, FontSize = 12 };
        _vtNotice = new InfoBar { IsOpen = false, IsClosable = true };
        _vtDelete = new Button { Content = "删除模型" };
        _vtDelete.Click += async (_, _) => await OnVoiceDeleteTtsAsync();
        _vtSave = new Button { Content = "保存 TTS 模型" };
        _vtSave.Click += async (_, _) => await SaveVoiceTtsAsync();
        var newTts = new Button { Content = "新建 TTS 模型" };
        newTts.Click += (_, _) => OnVoiceNewTts();
        var refreshTts = new Button { Content = "刷新" };
        refreshTts.Click += async (_, _) => await LoadVoiceAsync();

        VoiceTtsSettings.Content = new VoicePanel(
            "TTS 模型与能力", "音色、格式、采样率与能力开关逐项保留；未提交数组时沿用现有值，提交空数组才是清空。",
            _vtProvider, _vtPicker, Row(newTts, _vtDelete, refreshTts),
            _vtId, _vtName, _vtPath, _vtVoices, _vtFormats, _vtRates, _vtSort,
            Row(_vtStreaming, _vtInstructions), Row(_vtCloning, _vtDesign), Row(_vtDefault, _vtDeprecated),
            _vtDefaults, _vtSave, _vtNotice);

        _vaProvider = new ComboBox { Header = "服务商", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_vaProvider, "ASR 模型所属服务商");
        _vaProvider.SelectionChanged += (_, _) => { if (Selected(_vaProvider) is { } id) _ = LoadVoiceModelsAsync(id); };
        _vaPicker = new ComboBox { Header = "ASR 模型", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_vaPicker, "选择 ASR 模型");
        _vaPicker.SelectionChanged += (_, _) => ApplyVoiceAsrSelection();
        _vaId = Field("模型 ID", "例如 qwen3-asr-flash-realtime");
        _vaName = Field("模型名称");
        _vaPath = Field("Path", "/api/v1/services/aigc/multimodal-generation/generation");
        _vaLanguages = Field("支持语言", "逗号分隔，例如 zh-CN, en-US");
        _vaRates = Field("采样率", "逗号分隔，例如 16000");
        _vaSort = Field("排序", "0");
        _vaEmotion = new ToggleSwitch { Header = "情绪识别", OnContent = "支持", OffContent = "不支持" };
        _vaTimestamps = new ToggleSwitch { Header = "时间戳", OnContent = "支持", OffContent = "不支持" };
        _vaHotWords = new ToggleSwitch { Header = "热词", OnContent = "支持", OffContent = "不支持" };
        _vaDefault = new ToggleSwitch { Header = "设为默认 ASR 模型", OnContent = "默认", OffContent = "非默认" };
        _vaDeprecated = new ToggleSwitch { Header = "已废弃", OnContent = "已废弃", OffContent = "可用" };
        _vaDefaults = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = .7, FontSize = 12 };
        _vaNotice = new InfoBar { IsOpen = false, IsClosable = true };
        _vaDelete = new Button { Content = "删除模型" };
        _vaDelete.Click += async (_, _) => await OnVoiceDeleteAsrAsync();
        _vaSave = new Button { Content = "保存 ASR 模型" };
        _vaSave.Click += async (_, _) => await SaveVoiceAsrAsync();
        var newAsr = new Button { Content = "新建 ASR 模型" };
        newAsr.Click += (_, _) => OnVoiceNewAsr();
        var refreshAsr = new Button { Content = "刷新" };
        refreshAsr.Click += async (_, _) => await LoadVoiceAsync();

        VoiceAsrSettings.Content = new VoicePanel(
            "ASR 模型与能力", "语言、采样率与能力开关逐项保留；TTS 与 ASR 的默认项互不覆盖。",
            _vaProvider, _vaPicker, Row(newAsr, _vaDelete, refreshAsr),
            _vaId, _vaName, _vaPath, _vaLanguages, _vaRates, _vaSort,
            Row(_vaEmotion, _vaTimestamps, _vaHotWords), Row(_vaDefault, _vaDeprecated),
            _vaDefaults, _vaSave, _vaNotice);
        SetVoiceEnabled(false);
    }

    /// <summary>Renders a card-like settings panel; kept separate from the LLM panel builder for clarity.</summary>
    private sealed class VoicePanel : StackPanel
    {
        public VoicePanel(string title, string description, params UIElement[] children)
        {
            Spacing = 14;
            Margin = new Thickness(0, 16, 8, 16);
            Children.Add(Card(title, description, children));
        }
    }

    private static string? Selected(ComboBox picker) => (picker.SelectedItem as ComboBoxItem)?.Tag as string;

    internal async Task LoadVoiceAsync()
    {
        if (!_voiceBuilt || _voiceLoading) return;
        _voiceLoading = true;
        try
        {
            _voiceProviders = await _voiceSettings.ListProvidersAsync();
            _voiceDefaults = await _voiceSettings.GetDefaultsAsync();
            FillVoicePickers();
            SetVoiceEnabled(true);
            _vpNotice.IsOpen = false;
            _vtNotice.IsOpen = false;
            _vaNotice.IsOpen = false;
        }
        catch (Exception exception) { ReportVoiceFailure(exception); }
        finally { _voiceLoading = false; }
    }

    private async Task LoadVoiceModelsAsync(string providerId)
    {
        try
        {
            _voiceTts = await _voiceSettings.ListTtsModelsAsync(providerId);
            _voiceAsr = await _voiceSettings.ListAsrModelsAsync(providerId);
            FillModelPicker(_vtPicker, _voiceTts.Select(model => (model.ModelId, $"{model.Name}（{model.ModelId}）")).ToArray());
            FillModelPicker(_vaPicker, _voiceAsr.Select(model => (model.ModelId, $"{model.Name}（{model.ModelId}）")).ToArray());
            if (_voiceTts.Count == 0) { _voiceTtsModel = null; FillVoiceTtsForm(null); }
            if (_voiceAsr.Count == 0) { _voiceAsrModel = null; FillVoiceAsrForm(null); }
            _vtNotice.IsOpen = false;
            _vaNotice.IsOpen = false;
        }
        catch (Exception exception) { ReportVoiceFailure(exception); }
    }

    private static void FillModelPicker(ComboBox picker, IReadOnlyList<(string Id, string Label)> items)
    {
        picker.Items.Clear();
        foreach (var (id, label) in items) picker.Items.Add(new ComboBoxItem { Content = label, Tag = id });
        picker.SelectedIndex = items.Count > 0 ? 0 : -1;
    }

    private void FillVoicePickers()
    {
        var previous = _voiceProvider?.ProviderId;
        foreach (var picker in new[] { _vpPicker, _vtProvider, _vaProvider }) picker.Items.Clear();
        foreach (var provider in _voiceProviders)
            foreach (var picker in new[] { _vpPicker, _vtProvider, _vaProvider })
                picker.Items.Add(new ComboBoxItem { Content = $"{provider.Name}（{provider.ProviderId}）", Tag = provider.ProviderId });
        var found = _voiceProviders.ToList().FindIndex(provider => provider.ProviderId == previous);
        var index = _voiceProviders.Count > 0 ? Math.Max(0, found) : -1;
        _vtProvider.SelectedIndex = index;
        _vaProvider.SelectedIndex = index;
        _vpPicker.SelectedIndex = index;
        ApplyVoiceDefaults();
        if (index >= 0) return;
        _voiceProvider = null; _voiceTtsModel = null; _voiceAsrModel = null;
        FillVoiceProviderForm(null); FillVoiceTtsForm(null); FillVoiceAsrForm(null);
    }

    private void ApplyVoiceDefaults()
    {
        var text = _voiceDefaults.Describe();
        _vtDefaults.Text = text;
        _vaDefaults.Text = text;
    }

    private void ApplyVoiceProviderSelection()
    {
        _voiceProvider = Selected(_vpPicker) is { } id
            ? _voiceProviders.FirstOrDefault(provider => provider.ProviderId == id)
            : null;
        FillVoiceProviderForm(_voiceProvider);
        if (_voiceProvider is null) return;
        if (!string.Equals(Selected(_vtProvider), _voiceProvider.ProviderId, StringComparison.Ordinal))
        {
            var index = _voiceProviders.ToList().FindIndex(provider => provider.ProviderId == _voiceProvider.ProviderId);
            if (index >= 0) _vtProvider.SelectedIndex = index;
        }
        if (!string.Equals(Selected(_vaProvider), _voiceProvider.ProviderId, StringComparison.Ordinal))
        {
            var index = _voiceProviders.ToList().FindIndex(provider => provider.ProviderId == _voiceProvider.ProviderId);
            if (index >= 0) _vaProvider.SelectedIndex = index;
        }
    }

    private void FillVoiceProviderForm(VoiceProviderSummary? provider)
    {
        _vpId.Text = provider?.ProviderId ?? "";
        _vpId.IsReadOnly = provider is not null;
        _vpName.Text = provider?.Name ?? "";
        _vpEndpoint.Text = provider?.Endpoint ?? "";
        _vpDescription.Text = provider?.Description ?? "";
        _vpEnabled.IsOn = provider?.IsEnabled ?? true;
        _vpKeyChange.SelectedIndex = 0;
        _vpNewKey.Password = "";
        _vpSummary.Text = provider is null ? "" : $"TTS 模型 {provider.TtsModelCount} 个 · ASR 模型 {provider.AsrModelCount} 个";
        UpdateVoiceKeyEditor();
        _vpDelete.IsEnabled = provider is not null;
    }

    private void UpdateVoiceKeyEditor()
    {
        var change = _vpKeyChange.SelectedItem is ComboBoxItem { Tag: ApiKeyChange value } ? value : ApiKeyChange.Keep;
        _vpNewKey.Visibility = change == ApiKeyChange.Replace ? Visibility.Visible : Visibility.Collapsed;
        if (change != ApiKeyChange.Replace) _vpNewKey.Password = "";
        _vpKeyState.Text = LlmSettingsText.DescribeKeyState(_voiceProvider?.HasApiKey ?? false, change);
    }

    private void OnVoiceNewProvider()
    {
        _vpPicker.SelectedIndex = -1;
        _voiceProvider = null;
        FillVoiceProviderForm(null);
        _vpNotice.IsOpen = false;
    }

    private async Task SaveVoiceProviderAsync()
    {
        var change = _vpKeyChange.SelectedItem is ComboBoxItem { Tag: ApiKeyChange value } ? value : ApiKeyChange.Keep;
        var edit = new VoiceProviderEdit(_vpId.Text.Trim(), _vpName.Text.Trim(), _vpEndpoint.Text.Trim(),
            _vpDescription.Text.Trim(), _vpEnabled.IsOn, change, _vpNewKey.Password);
        var errors = VoiceSettingsText.Validate(edit);
        if (errors.Count > 0) { ShowNotice(_vpNotice, InfoBarSeverity.Warning, "请先修正表单", string.Join(" ", errors)); return; }
        await RunVoiceAsync(_vpNotice, "语音服务商已保存", "已写入 config/voice/providers.json；密钥不返回明文。",
            async () => { await _voiceSettings.SaveProviderAsync(edit); await LoadVoiceAsync(); });
    }

    private async Task OnVoiceDeleteProviderAsync()
    {
        if (_voiceProvider is not { } provider) return;
        var confirm = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = "删除语音服务商",
            Content = $"将删除 {provider.Name}（{provider.ProviderId}）及其 {provider.TtsModelCount} 个 TTS、{provider.AsrModelCount} 个 ASR 模型；" +
                      "指向它的默认项会被清空。此操作不可撤销。",
            PrimaryButtonText = "删除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        await RunVoiceAsync(_vpNotice, "语音服务商已删除", "关联模型与默认指针已由 Core 一并清理。",
            async () => { await _voiceSettings.DeleteProviderAsync(provider.ProviderId); await LoadVoiceAsync(); });
    }

    private void ApplyVoiceTtsSelection()
    {
        _voiceTtsModel = Selected(_vtPicker) is { } id ? _voiceTts.FirstOrDefault(model => model.ModelId == id) : null;
        FillVoiceTtsForm(_voiceTtsModel);
    }

    private void FillVoiceTtsForm(VoiceTtsModel? model)
    {
        _vtId.Text = model?.ModelId ?? "";
        _vtId.IsReadOnly = model is not null;
        _vtName.Text = model?.Name ?? "";
        _vtPath.Text = model?.Path ?? "";
        _vtVoices.Text = VoiceSettingsText.FormatList(model?.Voices);
        _vtFormats.Text = VoiceSettingsText.FormatList(model?.AudioFormats);
        _vtRates.Text = VoiceSettingsText.FormatSampleRates(model?.SampleRates);
        _vtSort.Text = (model?.SortOrder ?? 0).ToString();
        _vtStreaming.IsOn = model?.SupportsStreaming ?? false;
        _vtInstructions.IsOn = model?.SupportsInstructions ?? false;
        _vtCloning.IsOn = model?.SupportsVoiceCloning ?? false;
        _vtDesign.IsOn = model?.SupportsVoiceDesign ?? false;
        _vtDefault.IsOn = model?.IsDefault ?? false;
        _vtDeprecated.IsOn = model?.IsDeprecated ?? false;
        _vtDelete.IsEnabled = model is not null;
    }

    private void OnVoiceNewTts()
    {
        _vtPicker.SelectedIndex = -1;
        _voiceTtsModel = null;
        FillVoiceTtsForm(null);
        _vtNotice.IsOpen = false;
    }

    private async Task SaveVoiceTtsAsync()
    {
        var providerId = Selected(_vtProvider) ?? "";
        var model = new VoiceTtsModel(providerId, _vtId.Text.Trim(), _vtName.Text.Trim(), _vtPath.Text.Trim(),
            VoiceSettingsText.ParseList(_vtVoices.Text), VoiceSettingsText.ParseList(_vtFormats.Text),
            VoiceSettingsText.ParseSampleRates(_vtRates.Text),
            _vtStreaming.IsOn, _vtInstructions.IsOn, _vtCloning.IsOn, _vtDesign.IsOn,
            _vtDeprecated.IsOn, _vtDefault.IsOn, VoiceSettingsText.ParseOptionalInt(_vtSort.Text) ?? 0);
        var errors = VoiceSettingsText.Validate(model);
        if (errors.Count > 0) { ShowNotice(_vtNotice, InfoBarSeverity.Warning, "请先修正表单", string.Join(" ", errors)); return; }
        await RunVoiceAsync(_vtNotice, "TTS 模型已保存", "音色、格式、采样率与能力开关已写入；ASR 默认项未受影响。",
            async () => { await _voiceSettings.SaveTtsModelAsync(model); await ReloadVoiceAsync(providerId); });
    }

    private async Task OnVoiceDeleteTtsAsync()
    {
        if (_voiceTtsModel is not { } model) return;
        if (!await ConfirmVoiceDeleteAsync("删除 TTS 模型", model.Name, model.ModelId)) return;
        await RunVoiceAsync(_vtNotice, "TTS 模型已删除", "若它是默认 TTS 模型，默认真值已一并清空。",
            async () => { await _voiceSettings.DeleteTtsModelAsync(model.ProviderId, model.ModelId); await ReloadVoiceAsync(model.ProviderId); });
    }

    private void ApplyVoiceAsrSelection()
    {
        _voiceAsrModel = Selected(_vaPicker) is { } id ? _voiceAsr.FirstOrDefault(model => model.ModelId == id) : null;
        FillVoiceAsrForm(_voiceAsrModel);
    }

    private void FillVoiceAsrForm(VoiceAsrModel? model)
    {
        _vaId.Text = model?.ModelId ?? "";
        _vaId.IsReadOnly = model is not null;
        _vaName.Text = model?.Name ?? "";
        _vaPath.Text = model?.Path ?? "";
        _vaLanguages.Text = VoiceSettingsText.FormatList(model?.Languages);
        _vaRates.Text = VoiceSettingsText.FormatSampleRates(model?.SampleRates);
        _vaSort.Text = (model?.SortOrder ?? 0).ToString();
        _vaEmotion.IsOn = model?.SupportsEmotion ?? false;
        _vaTimestamps.IsOn = model?.SupportsTimestamps ?? false;
        _vaHotWords.IsOn = model?.SupportsHotWords ?? false;
        _vaDefault.IsOn = model?.IsDefault ?? false;
        _vaDeprecated.IsOn = model?.IsDeprecated ?? false;
        _vaDelete.IsEnabled = model is not null;
    }

    private void OnVoiceNewAsr()
    {
        _vaPicker.SelectedIndex = -1;
        _voiceAsrModel = null;
        FillVoiceAsrForm(null);
        _vaNotice.IsOpen = false;
    }

    private async Task SaveVoiceAsrAsync()
    {
        var providerId = Selected(_vaProvider) ?? "";
        var model = new VoiceAsrModel(providerId, _vaId.Text.Trim(), _vaName.Text.Trim(), _vaPath.Text.Trim(),
            VoiceSettingsText.ParseList(_vaLanguages.Text), VoiceSettingsText.ParseSampleRates(_vaRates.Text),
            _vaEmotion.IsOn, _vaTimestamps.IsOn, _vaHotWords.IsOn,
            _vaDeprecated.IsOn, _vaDefault.IsOn, VoiceSettingsText.ParseOptionalInt(_vaSort.Text) ?? 0);
        var errors = VoiceSettingsText.Validate(model);
        if (errors.Count > 0) { ShowNotice(_vaNotice, InfoBarSeverity.Warning, "请先修正表单", string.Join(" ", errors)); return; }
        await RunVoiceAsync(_vaNotice, "ASR 模型已保存", "语言、采样率与能力开关已写入；TTS 默认项未受影响。",
            async () => { await _voiceSettings.SaveAsrModelAsync(model); await ReloadVoiceAsync(providerId); });
    }

    private async Task OnVoiceDeleteAsrAsync()
    {
        if (_voiceAsrModel is not { } model) return;
        if (!await ConfirmVoiceDeleteAsync("删除 ASR 模型", model.Name, model.ModelId)) return;
        await RunVoiceAsync(_vaNotice, "ASR 模型已删除", "若它是默认 ASR 模型，默认真值已一并清空。",
            async () => { await _voiceSettings.DeleteAsrModelAsync(model.ProviderId, model.ModelId); await ReloadVoiceAsync(model.ProviderId); });
    }

    private async Task<bool> ConfirmVoiceDeleteAsync(string title, string name, string modelId)
    {
        var confirm = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = title,
            Content = $"将删除 {name}（{modelId}）。若它是当前默认模型，默认真值会被清空。此操作不可撤销。",
            PrimaryButtonText = "删除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
        };
        return await confirm.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task ReloadVoiceAsync(string providerId)
    {
        _voiceDefaults = await _voiceSettings.GetDefaultsAsync();
        ApplyVoiceDefaults();
        await LoadVoiceModelsAsync(providerId);
    }

    private async Task RunVoiceAsync(InfoBar notice, string title, string message, Func<Task> action)
    {
        SetVoiceEnabled(false);
        try
        {
            await action();
            SetVoiceEnabled(true);
            ShowNotice(notice, InfoBarSeverity.Success, title, message);
        }
        catch (Exception exception) { ReportVoiceFailure(exception); }
    }

    private void ReportVoiceFailure(Exception exception)
    {
        App.WriteDiagnostic(exception);
        var unavailable = exception is SettingsUnavailableException;
        var (severity, title, message) = exception switch
        {
            SettingsUnavailableException reason => (InfoBarSeverity.Informational, "Core 未就绪",
                reason.Message + " 该分类仍可浏览；表单已禁用，未写入任何内容。"),
            SettingsConflictException conflict => (InfoBarSeverity.Warning, "版本冲突", conflict.Message),
            ArgumentException argument => (InfoBarSeverity.Warning, "表单或配置被 Core 拒绝", argument.Message),
            InvalidOperationException invalid => (InfoBarSeverity.Warning, "操作未完成", invalid.Message),
            _ => (InfoBarSeverity.Error, "操作失败", "请查看诊断日志后重试。")
        };
        SetVoiceEnabled(!unavailable);
        // Never claim "未设置" when the truth is unknown: say why the defaults cannot be read yet.
        if (unavailable)
        {
            _vtDefaults.Text = DefaultsUnavailable;
            _vaDefaults.Text = DefaultsUnavailable;
        }
        ShowNotice(_vpNotice, severity, title, message);
        ShowNotice(_vtNotice, severity, title, message);
        ShowNotice(_vaNotice, severity, title, message);
    }

    private const string DefaultsUnavailable = "默认 TTS 与默认 ASR：Core 未就绪，暂时无法读取。";

    private void SetVoiceEnabled(bool enabled)
    {
        foreach (var control in new Control[]
        {
            _vpPicker, _vpId, _vpName, _vpEndpoint, _vpDescription, _vpEnabled, _vpKeyChange, _vpNewKey, _vpSave,
            _vtProvider, _vtPicker, _vtId, _vtName, _vtPath, _vtVoices, _vtFormats, _vtRates, _vtSort,
            _vtStreaming, _vtInstructions, _vtCloning, _vtDesign, _vtDefault, _vtDeprecated, _vtSave,
            _vaProvider, _vaPicker, _vaId, _vaName, _vaPath, _vaLanguages, _vaRates, _vaSort,
            _vaEmotion, _vaTimestamps, _vaHotWords, _vaDefault, _vaDeprecated, _vaSave
        }) control.IsEnabled = enabled;
        if (!enabled) { _vpDelete.IsEnabled = false; _vtDelete.IsEnabled = false; _vaDelete.IsEnabled = false; return; }
        _vpDelete.IsEnabled = _voiceProvider is not null;
        _vtDelete.IsEnabled = _voiceTtsModel is not null;
        _vaDelete.IsEnabled = _voiceAsrModel is not null;
    }
}
