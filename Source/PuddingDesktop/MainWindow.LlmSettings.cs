using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PuddingDesktop.Foundation;

namespace PuddingDesktop;

/// <summary>
/// DS-02 native LLM resource-pool settings: providers, provider limits, model definitions and
/// model context/pricing. Every call goes through ILlmResourceSettings into the in-process Core;
/// authoritative validation, atomic writes and the write lock stay there.
/// </summary>
public sealed partial class MainWindow
{
    private IReadOnlyList<LlmProviderSummary> _llmProviders = [];
    private IReadOnlyList<LlmModelSummary> _llmModels = [];
    private LlmProviderSummary? _llmProvider;
    private LlmModelSummary? _llmModel;
    private bool _llmLoading;
    private bool _llmBuilt;

    private ComboBox _llmProviderPicker = null!;
    private TextBox _llmProviderId = null!, _llmProviderName = null!, _llmProviderUrl = null!, _llmProviderDescription = null!;
    private TextBox _llmProviderConcurrency = null!, _llmProviderTpm = null!, _llmProviderRpm = null!;
    private ComboBox _llmKeyChange = null!;
    private PasswordBox _llmNewKey = null!;
    private ToggleSwitch _llmProviderEnabled = null!;
    private TextBlock _llmKeyState = null!;
    private InfoBar _llmProviderNotice = null!;
    private Button _llmProviderSave = null!, _llmProviderDelete = null!;

    private ComboBox _llmModelProvider = null!, _llmModelPicker = null!, _llmProtocol = null!;
    private TextBox _llmModelId = null!, _llmModelName = null!, _llmTags = null!, _llmSortOrder = null!;
    private TextBox _llmContext = null!, _llmInputTokens = null!, _llmOutputTokens = null!, _llmModelConcurrency = null!;
    private TextBox _llmInputPrice = null!, _llmOutputPrice = null!, _llmCachePrice = null!;
    private ToggleSwitch _llmIsDefault = null!, _llmIsDeprecated = null!, _llmIsEmbedding = null!;
    private InfoBar _llmModelNotice = null!;
    private Button _llmModelSave = null!, _llmModelDelete = null!;

    private void BuildLlmPanels()
    {
        if (_llmBuilt) return;
        _llmBuilt = true;

        _llmProviderPicker = new ComboBox { Header = "服务商", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_llmProviderPicker, "选择服务商");
        _llmProviderPicker.SelectionChanged += (_, _) => ApplyProviderSelection();

        _llmProviderId = Field("服务商 ID", "例如 deepseek");
        _llmProviderName = Field("名称");
        _llmProviderUrl = Field("BaseUrl", "https://api.example.com/v1");
        _llmProviderDescription = Field("描述");

        _llmKeyChange = new ComboBox { Header = "API Key", HorizontalAlignment = HorizontalAlignment.Stretch };
        _llmKeyChange.Items.Add(new ComboBoxItem { Content = "保持现有密钥", Tag = ApiKeyChange.Keep });
        _llmKeyChange.Items.Add(new ComboBoxItem { Content = "替换为新密钥", Tag = ApiKeyChange.Replace });
        _llmKeyChange.Items.Add(new ComboBoxItem { Content = "清除已保存的密钥", Tag = ApiKeyChange.Clear });
        _llmKeyChange.SelectedIndex = 0;
        _llmKeyChange.SelectionChanged += (_, _) => UpdateKeyEditor();
        _llmNewKey = new PasswordBox { Header = "新密钥（仅在替换时写入）", Visibility = Visibility.Collapsed };
        AutomationProperties.SetName(_llmNewKey, "新密钥");
        _llmKeyState = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = .7, FontSize = 12 };
        _llmProviderEnabled = new ToggleSwitch { Header = "启用该服务商", OnContent = "已启用", OffContent = "已停用", IsOn = true };

        _llmProviderConcurrency = Field("最大并发请求数", "留空表示不限制");
        _llmProviderTpm = Field("每分钟 Token 上限（TPM）", "留空表示不限制");
        _llmProviderRpm = Field("每分钟请求上限（RPM）", "留空表示不限制");

        _llmProviderNotice = new InfoBar { IsOpen = false, IsClosable = true };
        var newProvider = new Button { Content = "新建服务商" };
        newProvider.Click += (_, _) => OnLlmNewProvider();
        _llmProviderDelete = new Button { Content = "删除服务商" };
        _llmProviderDelete.Click += async (_, _) => await OnLlmDeleteProviderAsync();
        var refresh = new Button { Content = "刷新" };
        refresh.Click += async (_, _) => await LoadLlmAsync();
        _llmProviderSave = new Button { Content = "保存服务商与限流" };
        _llmProviderSave.Click += async (_, _) => await SaveLlmProviderAsync();

        LlmProvidersSettings.Content = new StackPanel
        {
            Spacing = 14,
            Margin = new Thickness(0, 16, 8, 16),
            Children =
            {
                Card("服务商连接", "新增、编辑、启停和删除服务商；删除前先确认关联模型数量。密钥只显示是否已配置，不显示明文。",
                    _llmProviderPicker, Row(newProvider, _llmProviderDelete, refresh),
                    _llmProviderId, _llmProviderName, _llmProviderUrl, _llmProviderDescription, _llmProviderEnabled,
                    _llmKeyChange, _llmNewKey, _llmKeyState),
                Card("并发与速率", "服务商级限流。留空表示该项不写入配置，保存时不会用 0 覆盖；未展示的超时、重试与熔断参数保持原值。",
                    _llmProviderConcurrency, _llmProviderTpm, _llmProviderRpm, _llmProviderSave),
                _llmProviderNotice
            }
        };

        _llmModelProvider = new ComboBox { Header = "服务商", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_llmModelProvider, "模型所属服务商");
        _llmModelProvider.SelectionChanged += (_, _) => { if (_llmModelProvider.SelectedItem is ComboBoxItem { Tag: string id }) _ = LoadLlmModelsAsync(id); };
        _llmModelPicker = new ComboBox { Header = "模型", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_llmModelPicker, "选择模型");
        _llmModelPicker.SelectionChanged += (_, _) => ApplyModelSelection();

        _llmModelId = Field("模型 ID", "例如 deepseek-chat");
        _llmModelName = Field("模型名称");
        _llmProtocol = new ComboBox { Header = "协议", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var protocol in LlmSettingsText.Protocols) _llmProtocol.Items.Add(new ComboBoxItem { Content = protocol, Tag = protocol });
        _llmProtocol.SelectedIndex = 0;
        _llmTags = Field("能力标签", "逗号分隔，例如 tools, vision");
        _llmSortOrder = Field("排序", "0");
        _llmIsDefault = new ToggleSwitch { Header = "默认模型", OnContent = "默认", OffContent = "非默认" };
        _llmIsDeprecated = new ToggleSwitch { Header = "已废弃", OnContent = "已废弃", OffContent = "可用" };
        _llmIsEmbedding = new ToggleSwitch { Header = "Embedding 模型", OnContent = "Embedding", OffContent = "对话模型" };
        _llmModelConcurrency = Field("模型级最大并发", "留空表示继承服务商");
        _llmContext = Field("最大上下文 token");
        _llmInputTokens = Field("最大输入 token", "留空表示未设置");
        _llmOutputTokens = Field("最大输出 token");
        _llmInputPrice = Field("输入价格 / 1M token");
        _llmOutputPrice = Field("输出价格 / 1M token");
        _llmCachePrice = Field("缓存命中价格 / 1M token");

        _llmModelNotice = new InfoBar { IsOpen = false, IsClosable = true };
        var newModel = new Button { Content = "新建模型" };
        newModel.Click += (_, _) => OnLlmNewModel();
        _llmModelDelete = new Button { Content = "删除模型" };
        _llmModelDelete.Click += async (_, _) => await OnLlmDeleteModelAsync();
        var refreshModels = new Button { Content = "刷新" };
        refreshModels.Click += async (_, _) => await LoadLlmAsync();
        _llmModelSave = new Button { Content = "保存模型定义与计费" };
        _llmModelSave.Click += async (_, _) => await SaveLlmModelAsync();

        LlmModelsSettings.Content = new StackPanel
        {
            Spacing = 14,
            Margin = new Thickness(0, 16, 8, 16),
            Children =
            {
                Card("模型定义", "按服务商筛选模型并编辑定义。保存会提交整张表单，因此上下文的价格字段不会因为只改名称而丢失。",
                    _llmModelProvider, _llmModelPicker, Row(newModel, _llmModelDelete, refreshModels),
                    _llmModelId, _llmModelName, _llmProtocol, _llmTags, _llmSortOrder,
                    Row(_llmIsDefault, _llmIsDeprecated, _llmIsEmbedding)),
                Card("上下文与计费", "保留 Core 的单位与范围校验；已废弃模型的版本化价格窗口与来源字段不会被清空。",
                    _llmContext, _llmInputTokens, _llmOutputTokens, _llmModelConcurrency,
                    _llmInputPrice, _llmOutputPrice, _llmCachePrice, _llmModelSave),
                new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap, Opacity = .7, FontSize = 12,
                    Text = "已登记缺口：Core 的模型配置没有 description 字段，因此本页不提供模型描述输入，避免出现不生效的控件。"
                },
                _llmModelNotice
            }
        };
        SetLlmEnabled(false);
    }

    private static TextBox Field(string header, string placeholder = "") => new()
    {
        Header = header, PlaceholderText = placeholder, HorizontalAlignment = HorizontalAlignment.Stretch
    };

    private static StackPanel Row(params UIElement[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var child in children) row.Children.Add(child);
        return row;
    }

    private static Border Card(string title, string description, params UIElement[] children)
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Opacity = .75 });
        foreach (var child in children) panel.Children.Add(child);
        var border = (Border)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            "<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
            "Background='{ThemeResource CardBackground}' BorderBrush='{ThemeResource DividerBrush}' " +
            "BorderThickness='1' CornerRadius='12' Padding='22' />");
        border.Child = panel;
        return border;
    }

    /// <summary>Loads providers and models for the visible LLM tab; safe to call repeatedly.</summary>
    internal async Task LoadLlmAsync()
    {
        if (!_llmBuilt || _llmLoading) return;
        _llmLoading = true;
        try
        {
            var providers = await _llmSettings.ListProvidersAsync();
            _llmProviders = providers;
            FillProviderPickers();
            SetLlmEnabled(true);
            _llmProviderNotice.IsOpen = false;
            _llmModelNotice.IsOpen = false;
        }
        catch (Exception exception) { ReportLlmFailure(exception); }
        finally { _llmLoading = false; }
    }

    private async Task LoadLlmModelsAsync(string providerId)
    {
        try
        {
            _llmModels = await _llmSettings.ListModelsAsync(providerId);
            _llmModelPicker.Items.Clear();
            foreach (var model in _llmModels)
                _llmModelPicker.Items.Add(new ComboBoxItem { Content = $"{model.Name}（{model.ModelId}）", Tag = model.ModelId });
            _llmModelPicker.SelectedIndex = _llmModels.Count > 0 ? 0 : -1;
            if (_llmModels.Count == 0) { _llmModel = null; FillModelForm(null); }
            _llmModelNotice.IsOpen = false;
        }
        catch (Exception exception) { ReportLlmFailure(exception); }
    }

    private void FillProviderPickers()
    {
        var previous = _llmProvider?.ProviderId;
        _llmProviderPicker.Items.Clear();
        _llmModelProvider.Items.Clear();
        foreach (var provider in _llmProviders)
        {
            _llmProviderPicker.Items.Add(new ComboBoxItem { Content = $"{provider.Name}（{provider.ProviderId}）", Tag = provider.ProviderId });
            _llmModelProvider.Items.Add(new ComboBoxItem { Content = $"{provider.Name}（{provider.ProviderId}）", Tag = provider.ProviderId });
        }
        var found = _llmProviders.ToList().FindIndex(provider => provider.ProviderId == previous);
        var index = _llmProviders.Count > 0 ? Math.Max(0, found) : -1;
        // Select the model provider first: it triggers the model load for that provider.
        _llmModelProvider.SelectedIndex = index;
        _llmProviderPicker.SelectedIndex = index;
        if (index >= 0) return;
        _llmProvider = null; _llmModel = null;
        FillProviderForm(null); FillModelForm(null);
    }

    private void ApplyProviderSelection()
    {
        _llmProvider = _llmProviderPicker.SelectedItem is ComboBoxItem { Tag: string id }
            ? _llmProviders.FirstOrDefault(provider => provider.ProviderId == id)
            : null;
        FillProviderForm(_llmProvider);
        if (_llmProvider is null) return;
        var modelProvider = (_llmModelProvider.SelectedItem as ComboBoxItem)?.Tag as string;
        if (string.Equals(modelProvider, _llmProvider.ProviderId, StringComparison.Ordinal)) return;
        var index = _llmProviders.ToList().FindIndex(provider => provider.ProviderId == _llmProvider.ProviderId);
        if (index >= 0) _llmModelProvider.SelectedIndex = index;
        else _ = LoadLlmModelsAsync(_llmProvider.ProviderId);
    }

    private void FillProviderForm(LlmProviderSummary? provider)
    {
        _llmProviderId.Text = provider?.ProviderId ?? "";
        _llmProviderId.IsReadOnly = provider is not null;
        _llmProviderName.Text = provider?.Name ?? "";
        _llmProviderUrl.Text = provider?.BaseUrl ?? "";
        _llmProviderDescription.Text = provider?.Description ?? "";
        _llmProviderEnabled.IsOn = provider?.IsEnabled ?? true;
        _llmProviderConcurrency.Text = LlmSettingsText.FormatOptional(provider?.Limits.MaxConcurrentRequests);
        _llmProviderTpm.Text = LlmSettingsText.FormatOptional(provider?.Limits.TokensPerMinute);
        _llmProviderRpm.Text = LlmSettingsText.FormatOptional(provider?.Limits.RequestsPerMinute);
        _llmKeyChange.SelectedIndex = 0;
        _llmNewKey.Password = "";
        UpdateKeyEditor();
        _llmProviderDelete.IsEnabled = provider is not null;
    }

    private void UpdateKeyEditor()
    {
        var change = _llmKeyChange.SelectedItem is ComboBoxItem { Tag: ApiKeyChange value } ? value : ApiKeyChange.Keep;
        _llmNewKey.Visibility = change == ApiKeyChange.Replace ? Visibility.Visible : Visibility.Collapsed;
        _llmNewKey.Password = change == ApiKeyChange.Replace ? _llmNewKey.Password : "";
        _llmKeyState.Text = LlmSettingsText.DescribeKeyState(_llmProvider?.HasApiKey ?? false, change);
    }

    private void OnLlmNewProvider()
    {
        _llmProviderPicker.SelectedIndex = -1;
        _llmProvider = null;
        FillProviderForm(null);
        _llmProviderNotice.IsOpen = false;
    }

    private async Task SaveLlmProviderAsync()
    {
        var change = _llmKeyChange.SelectedItem is ComboBoxItem { Tag: ApiKeyChange value } ? value : ApiKeyChange.Keep;
        var edit = new LlmProviderEdit(
            _llmProviderId.Text.Trim(), _llmProviderName.Text.Trim(), _llmProviderUrl.Text.Trim(),
            _llmProviderDescription.Text.Trim(), _llmProviderEnabled.IsOn, change, _llmNewKey.Password,
            new LlmProviderLimits(
                LlmSettingsText.ParseOptionalInt(_llmProviderConcurrency.Text),
                LlmSettingsText.ParseOptionalLong(_llmProviderTpm.Text),
                LlmSettingsText.ParseOptionalInt(_llmProviderRpm.Text)));
        var errors = LlmSettingsText.Validate(edit);
        if (errors.Count > 0) { ShowNotice(_llmProviderNotice, InfoBarSeverity.Warning, "请先修正表单", string.Join(" ", errors)); return; }
        await RunLlmAsync(_llmProviderNotice, "服务商已保存",
            "已写入 llm.providers.json；未展示的超时、重试、熔断与兼容参数保持原值。密钥不返回明文。",
            async () => { await _llmSettings.SaveProviderAsync(edit); await LoadLlmAsync(); });
    }

    private async Task OnLlmDeleteProviderAsync()
    {
        if (_llmProvider is not { } provider) return;
        var confirm = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = "删除服务商",
            Content = $"将删除 {provider.Name}（{provider.ProviderId}）及其 {provider.ModelCount} 个模型，并清除引用它的 profile/角色配置。此操作不可撤销。",
            PrimaryButtonText = "删除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        await RunLlmAsync(_llmProviderNotice, "服务商已删除", "关联模型与 profile 引用已由 Core 一并清理。",
            async () => { await _llmSettings.DeleteProviderAsync(provider.ProviderId); await LoadLlmAsync(); });
    }

    private void ApplyModelSelection()
    {
        _llmModel = _llmModelPicker.SelectedItem is ComboBoxItem { Tag: string id }
            ? _llmModels.FirstOrDefault(model => model.ModelId == id)
            : null;
        FillModelForm(_llmModel);
    }

    private void FillModelForm(LlmModelSummary? model)
    {
        _llmModelId.Text = model?.ModelId ?? "";
        _llmModelId.IsReadOnly = model is not null;
        _llmModelName.Text = model?.Name ?? "";
        var protocol = model?.Protocol ?? LlmSettingsText.Protocols[0];
        _llmProtocol.SelectedIndex = Math.Max(0, Array.IndexOf(LlmSettingsText.Protocols, protocol));
        _llmTags.Text = LlmSettingsText.FormatTags(model?.CapabilityTags);
        _llmSortOrder.Text = (model?.SortOrder ?? 0).ToString();
        _llmIsDefault.IsOn = model?.IsDefault ?? false;
        _llmIsDeprecated.IsOn = model?.IsDeprecated ?? false;
        _llmIsEmbedding.IsOn = model?.IsEmbedding ?? false;
        _llmContext.Text = LlmSettingsText.FormatOptional(model?.MaxContextTokens);
        _llmInputTokens.Text = LlmSettingsText.FormatOptional(model?.MaxInputTokens);
        _llmOutputTokens.Text = LlmSettingsText.FormatOptional(model?.MaxOutputTokens);
        _llmModelConcurrency.Text = LlmSettingsText.FormatOptional(model?.MaxConcurrentRequests);
        _llmInputPrice.Text = (model?.InputPricePer1MTokens ?? 0m).ToString(System.Globalization.CultureInfo.InvariantCulture);
        _llmOutputPrice.Text = (model?.OutputPricePer1MTokens ?? 0m).ToString(System.Globalization.CultureInfo.InvariantCulture);
        _llmCachePrice.Text = (model?.CacheHitPricePer1MTokens ?? 0m).ToString(System.Globalization.CultureInfo.InvariantCulture);
        _llmModelDelete.IsEnabled = model is not null;
    }

    private void OnLlmNewModel()
    {
        _llmModelPicker.SelectedIndex = -1;
        _llmModel = null;
        FillModelForm(null);
        _llmModelNotice.IsOpen = false;
    }

    private async Task SaveLlmModelAsync()
    {
        var providerId = (_llmModelProvider.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        var protocol = (_llmProtocol.SelectedItem as ComboBoxItem)?.Tag as string ?? LlmSettingsText.Protocols[0];
        var edit = new LlmModelEdit(
            providerId, _llmModelId.Text.Trim(), _llmModelName.Text.Trim(), protocol,
            LlmSettingsText.ParseTags(_llmTags.Text),
            LlmSettingsText.ParseOptionalInt(_llmContext.Text), LlmSettingsText.ParseOptionalInt(_llmInputTokens.Text),
            LlmSettingsText.ParseOptionalInt(_llmOutputTokens.Text), LlmSettingsText.ParseOptionalInt(_llmModelConcurrency.Text),
            LlmSettingsText.ParsePrice(_llmInputPrice.Text), LlmSettingsText.ParsePrice(_llmOutputPrice.Text),
            LlmSettingsText.ParsePrice(_llmCachePrice.Text),
            _llmIsDefault.IsOn, _llmIsDeprecated.IsOn, _llmIsEmbedding.IsOn,
            LlmSettingsText.ParseOptionalInt(_llmSortOrder.Text) ?? 0);
        var errors = LlmSettingsText.Validate(edit);
        if (errors.Count > 0) { ShowNotice(_llmModelNotice, InfoBarSeverity.Warning, "请先修正表单", string.Join(" ", errors)); return; }
        await RunLlmAsync(_llmModelNotice, "模型已保存",
            "已写入 llm.providers.json；同一服务商最多一个默认模型，未修改的版本化价格窗口保持原值。",
            async () => { await _llmSettings.SaveModelAsync(edit); await LoadLlmModelsAsync(edit.ProviderId); });
    }

    private async Task OnLlmDeleteModelAsync()
    {
        if (_llmModel is not { } model) return;
        var confirm = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = "删除模型",
            Content = $"将删除 {model.Name}（{model.ModelId}）。引用该模型的服务商 profile 会指向不存在的模型。此操作不可撤销。",
            PrimaryButtonText = "删除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        var providerId = (_llmModelProvider.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        await RunLlmAsync(_llmModelNotice, "模型已删除", "已从对应服务商移除。",
            async () => { await _llmSettings.DeleteModelAsync(providerId, model.ModelId); await LoadLlmModelsAsync(providerId); });
    }

    private async Task RunLlmAsync(InfoBar notice, string title, string message, Func<Task> action)
    {
        SetLlmEnabled(false);
        try
        {
            await action();
            SetLlmEnabled(true);
            ShowNotice(notice, InfoBarSeverity.Success, title, message);
        }
        catch (Exception exception) { ReportLlmFailure(exception); }
    }

    /// <summary>
    /// Never reports success when Core is unavailable: the real reason is shown and the form stays
    /// disabled because nothing was written. A form Core rejected keeps its draft and stays editable.
    /// </summary>
    private void ReportLlmFailure(Exception exception)
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
        SetLlmEnabled(!unavailable);
        ShowNotice(_llmProviderNotice, severity, title, message);
        ShowNotice(_llmModelNotice, severity, title, message);
    }

    private static void ShowNotice(InfoBar notice, InfoBarSeverity severity, string title, string message)
    {
        notice.Severity = severity;
        notice.Title = title;
        notice.Message = message;
        notice.IsOpen = true;
    }

    private void SetLlmEnabled(bool enabled)
    {
        foreach (var control in new Control[]
        {
            _llmProviderPicker, _llmProviderId, _llmProviderName, _llmProviderUrl, _llmProviderDescription,
            _llmProviderEnabled, _llmKeyChange, _llmNewKey, _llmProviderConcurrency, _llmProviderTpm, _llmProviderRpm,
            _llmProviderSave, _llmModelProvider, _llmModelPicker, _llmModelId, _llmModelName, _llmProtocol,
            _llmTags, _llmSortOrder, _llmIsDefault, _llmIsDeprecated, _llmIsEmbedding, _llmContext, _llmInputTokens,
            _llmOutputTokens, _llmModelConcurrency, _llmInputPrice, _llmOutputPrice, _llmCachePrice, _llmModelSave
        }) control.IsEnabled = enabled;
        if (!enabled) { _llmProviderDelete.IsEnabled = false; _llmModelDelete.IsEnabled = false; return; }
        _llmProviderDelete.IsEnabled = _llmProvider is not null;
        _llmModelDelete.IsEnabled = _llmModel is not null;
    }
}
