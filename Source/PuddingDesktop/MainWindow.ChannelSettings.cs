using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PuddingDesktop.Foundation;

namespace PuddingDesktop;

/// <summary>
/// DS-05 channel tab: channel providers and the channel instances bound to a workspace.
/// The App Secret is write-only here — the page shows whether one is stored, and a blank value keeps it.
/// </summary>
public sealed partial class MainWindow
{
    private IReadOnlyList<ChannelProvider> _chProviders = [];
    private IReadOnlyList<ChannelSummary> _chChannels = [];
    private IReadOnlyList<AgentWorkspaceOption> _chWorkspaces = [];
    private ChannelProvider? _chProvider;
    private ChannelSummary? _chChannel;
    private bool _chBuilt;
    private bool _chSwitching;

    private ComboBox _chProviderPicker = null!, _chWorkspacePicker = null!, _chChannelPicker = null!, _chProvider2 = null!;
    private TextBox _chProviderName = null!, _chProviderDescription = null!;
    private ToggleSwitch _chProviderEnabled = null!;
    private TextBlock _chProviderDetail = null!, _chChannelDetail = null!;
    private TextBox _chName = null!, _chDescription = null!, _chAgent = null!, _chAppId = null!;
    private ToggleSwitch _chReplaceSecret = null!;
    private PasswordBox _chSecret = null!;
    private ToggleSwitch _chStreaming = null!, _chTts = null!, _chEnabled = null!;
    private TextBox _chTtsVoice = null!, _chOpenIds = null!;
    private Button _chSave = null!, _chDelete = null!;
    private InfoBar _chNotice = null!;
    private InfoBar _chProviderNotice = null!;

    private void BuildChannelPanel()
    {
        if (_chBuilt) return;
        _chBuilt = true;

        _chProviderPicker = new ComboBox { Header = "渠道服务商", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_chProviderPicker, "选择渠道服务商");
        _chProviderPicker.SelectionChanged += (_, _) => { if (!_chSwitching) ApplyChannelProviderSelection(); };
        _chProviderName = Field("名称");
        _chProviderDescription = Field("描述");
        _chProviderEnabled = new ToggleSwitch { Header = "启用该服务商", OnContent = "已启用", OffContent = "已停用", IsOn = true };
        _chProviderDetail = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 12 };
        var saveProvider = new Button { Content = "保存服务商" };
        saveProvider.Click += async (_, _) => await SaveChannelProviderAsync();

        _chWorkspacePicker = new ComboBox { Header = "工作区", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_chWorkspacePicker, "渠道所属工作区");
        _chWorkspacePicker.SelectionChanged += async (_, _) => { if (!_chSwitching) await LoadChannelsAsync(); };
        _chChannelPicker = new ComboBox { Header = "渠道", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_chChannelPicker, "选择渠道");
        _chChannelPicker.SelectionChanged += (_, _) => { if (!_chSwitching) ApplyChannelSelection(); };
        var refresh = new Button { Content = "刷新渠道" };
        refresh.Click += async (_, _) => await LoadChannelsAsync();
        _chChannelDetail = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 12 };
        var create = new Button { Content = "新建渠道" };
        create.Click += async (_, _) => await CreateChannelAsync();

        _chName = Field("渠道名称");
        _chDescription = Field("描述");
        _chProvider2 = new ComboBox { Header = "服务商", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_chProvider2, "渠道使用的服务商");
        _chAgent = Field("绑定 Agent 实例 ID", "留空表示不绑定；必须属于当前工作区");
        _chAppId = Field("飞书 App ID");
        _chReplaceSecret = new ToggleSwitch
        {
            Header = "替换 App Secret", OnContent = "替换为下面的值", OffContent = "保持已保存的密钥", IsOn = false
        };
        _chSecret = new PasswordBox
        {
            Header = "App Secret（只写）", PasswordChar = "●", HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(_chSecret, "App Secret");
        _chReplaceSecret.Toggled += (_, _) => _chSecret.IsEnabled = _chReplaceSecret.IsOn;
        _chStreaming = new ToggleSwitch { Header = "流式回复", OnContent = "开", OffContent = "关", IsOn = true };
        _chTts = new ToggleSwitch { Header = "语音回复", OnContent = "开", OffContent = "关", IsOn = false };
        _chTtsVoice = Field("语音音色", ChannelText.TtsVoiceHint);
        _chOpenIds = Field("特权用户 Open ID", "逗号或空格分隔，留空表示没有特权用户");
        _chEnabled = new ToggleSwitch { Header = "启用该渠道", OnContent = "已启用", OffContent = "已停用", IsOn = true };
        _chSave = new Button { Content = "保存渠道" };
        _chSave.Click += async (_, _) => await SaveChannelAsync();
        _chDelete = new Button { Content = "删除渠道" };
        _chDelete.Click += async (_, _) => await DeleteChannelAsync();
        _chNotice = new InfoBar { IsOpen = false, IsClosable = true };
        _chProviderNotice = new InfoBar { IsOpen = false, IsClosable = true };

        ChannelProvidersSettings.Content = new StackPanel
        {
            Spacing = 14,
            Margin = new Thickness(0, 16, 8, 16),
            Children =
            {
                Card("渠道服务商", ChannelText.ProviderNotice,
                    _chProviderPicker, _chProviderName, _chProviderDescription, _chProviderEnabled,
                    _chProviderDetail, Row(saveProvider)),
                _chProviderNotice
            }
        };

        ChannelsSettings.Content = new StackPanel
        {
            Spacing = 14,
            Margin = new Thickness(0, 16, 8, 16),
            Children =
            {
                Card("渠道绑定与凭据", ChannelText.SecretNotice,
                    _chWorkspacePicker, Row(refresh), _chChannelPicker, _chChannelDetail,
                    _chName, _chDescription, _chProvider2, _chAgent, _chAppId,
                    _chReplaceSecret, _chSecret, _chStreaming, _chTts, _chTtsVoice, _chOpenIds, _chEnabled,
                    Row(_chSave, _chDelete), Row(create)),
                _chNotice
            }
        };
        SetChannelEnabled(false);
    }

    internal async Task LoadChannelProvidersAsync()
    {
        if (!_chBuilt) return;
        try
        {
            _chProviders = await _channels.ListProvidersAsync();
            _chSwitching = true;
            try
            {
                var previous = AgentSelected(_chProviderPicker);
                _chProviderPicker.Items.Clear();
                _chProvider2.Items.Clear();
                foreach (var provider in _chProviders)
                {
                    _chProviderPicker.Items.Add(new ComboBoxItem
                    {
                        Content = $"{provider.Name}（{provider.ProviderId}）· {provider.StateText}",
                        Tag = provider.ProviderId
                    });
                    _chProvider2.Items.Add(new ComboBoxItem
                    {
                        Content = $"{provider.Name}（{provider.ProviderId}）{(provider.IsEnabled ? "" : " · 已停用")}",
                        Tag = provider.ProviderId
                    });
                }
                var index = _chProviders.ToList().FindIndex(provider => provider.ProviderId == previous);
                _chProviderPicker.SelectedIndex = _chProviders.Count > 0 ? Math.Max(0, index) : -1;
                _chProvider2.SelectedIndex = _chProviders.Count > 0 ? 0 : -1;
            }
            finally { _chSwitching = false; }
            ApplyChannelProviderSelection();
            SetChannelEnabled(true);
            _chNotice.IsOpen = false;
        }
        catch (Exception exception) { ReportChannelFailure(exception); }
    }

    private void ApplyChannelProviderSelection()
    {
        _chProvider = AgentSelected(_chProviderPicker) is { } id
            ? _chProviders.FirstOrDefault(provider => provider.ProviderId == id)
            : null;
        var provider = _chProvider;
        _chProviderName.Text = provider?.Name ?? "";
        _chProviderDescription.Text = provider?.Description ?? "";
        _chProviderEnabled.IsOn = provider?.IsEnabled ?? true;
        _chProviderDetail.Text = provider is null
            ? (_chProviders.Count == 0 ? "Core 没有返回任何渠道服务商。" : "请选择一个服务商。")
            : $"类型 {provider.ChannelType} · 内置 {(provider.IsBuiltIn ? "是" : "否")} · 能力 {provider.CapabilityText}";
    }

    private async Task SaveChannelProviderAsync()
    {
        if (_chProvider is not { } provider) { WarnChannel("请先选择服务商"); return; }
        var edit = new ChannelProviderEdit(provider.ProviderId, _chProviderName.Text.Trim(), _chProviderDescription.Text.Trim(),
            _chProviderEnabled.IsOn);
        var errors = ChannelText.Validate(edit);
        if (errors.Count > 0) { ShowNotice(_chNotice, InfoBarSeverity.Warning, "请先修正表单", string.Join(" ", errors)); return; }
        await RunChannelAsync("服务商已保存", "只能修改名称、描述与启用状态；服务商本身由 Core 定义。",
            async () => { await _channels.SaveProviderAsync(edit); await LoadChannelProvidersAsync(); });
    }

    private async Task LoadChannelsAsync()
    {
        if (!_chBuilt) return;
        try
        {
            if (_chWorkspaces.Count == 0)
            {
                _chWorkspaces = await _agentDirectory.ListWorkspacesAsync();
                _chSwitching = true;
                try
                {
                    var previous = AgentSelected(_chWorkspacePicker);
                    _chWorkspacePicker.Items.Clear();
                    foreach (var workspace in _chWorkspaces)
                        _chWorkspacePicker.Items.Add(new ComboBoxItem
                        {
                            Content = $"{workspace.Name}（{workspace.WorkspaceId}）", Tag = workspace.WorkspaceId
                        });
                    var index = _chWorkspaces.ToList().FindIndex(workspace => workspace.WorkspaceId == previous);
                    _chWorkspacePicker.SelectedIndex = _chWorkspaces.Count > 0 ? Math.Max(0, index) : -1;
                }
                finally { _chSwitching = false; }
            }

            if (AgentSelected(_chWorkspacePicker) is not { } workspaceId)
            {
                _chChannelDetail.Text = "没有可用的工作区。";
                _chChannels = [];
                FillChannelPicker();
                return;
            }
            _chChannels = await _channels.ListChannelsAsync(workspaceId);
            FillChannelPicker();
            SetChannelEnabled(true);
            _chNotice.IsOpen = false;
        }
        catch (Exception exception) { ReportChannelFailure(exception); }
    }

    private void FillChannelPicker()
    {
        _chSwitching = true;
        try
        {
            var previous = AgentSelected(_chChannelPicker);
            _chChannelPicker.Items.Clear();
            foreach (var channel in _chChannels)
                _chChannelPicker.Items.Add(new ComboBoxItem
                {
                    Content = $"{channel.Name}（{channel.ProviderId}）· {channel.StateText}",
                    Tag = channel.ChannelId
                });
            var index = _chChannels.ToList().FindIndex(channel => channel.ChannelId == previous);
            _chChannelPicker.SelectedIndex = _chChannels.Count > 0 ? Math.Max(0, index) : -1;
        }
        finally { _chSwitching = false; }
        ApplyChannelSelection();
    }

    private void ApplyChannelSelection()
    {
        _chChannel = AgentSelected(_chChannelPicker) is { } id
            ? _chChannels.FirstOrDefault(channel => channel.ChannelId == id)
            : null;
        var channel = _chChannel;
        _chName.Text = channel?.Name ?? "";
        _chDescription.Text = channel?.Description ?? "";
        _chAgent.Text = channel?.BoundAgentId ?? "";
        _chAppId.Text = channel?.AppId ?? "";
        _chReplaceSecret.IsOn = false;
        _chSecret.Password = "";
        _chSecret.IsEnabled = false;
        _chStreaming.IsOn = channel?.StreamingRepliesEnabled ?? true;
        _chTts.IsOn = channel?.TtsRepliesEnabled ?? false;
        _chTtsVoice.Text = channel?.TtsVoice ?? "";
        _chOpenIds.Text = ChannelText.FormatOpenIds(channel?.PrivilegedUserOpenIds);
        _chEnabled.IsOn = channel?.IsEnabled ?? true;
        if (channel is not null)
        {
            var providerIndex = _chProviders.ToList().FindIndex(provider => provider.ProviderId == channel.ProviderId);
            if (providerIndex >= 0) _chProvider2.SelectedIndex = providerIndex;
        }
        _chChannelDetail.Text = channel is null
            ? (_chChannels.Count == 0 ? "该工作区还没有渠道。" : "请选择一个渠道。")
            : $"{channel.Name}（{channel.ChannelId}）· {channel.ProviderName} · {channel.StateText}\n" +
              $"App ID {channel.AppId} · App Secret {channel.SecretText}\n" +
              $"回复方式 {ChannelText.DescribeReplies(channel.StreamingRepliesEnabled, channel.TtsRepliesEnabled)}" +
              (channel.TtsRepliesEnabled ? $" · 音色 {channel.TtsVoice}" : "") + "\n" +
              $"绑定 Agent {(channel.BoundAgentId.Length == 0 ? "无" : channel.BoundAgentId)} · " +
              $"特权用户 {channel.PrivilegedUserOpenIds.Count} 人\n" +
              $"更新于 {channel.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm}\n" + ChannelText.SecretNotice;
        _chSave.IsEnabled = channel is not null;
        _chDelete.IsEnabled = channel is not null;
    }

    private ChannelEdit ReadChannelEdit(string workspaceId, string channelId) => new(
        workspaceId, channelId, _chName.Text.Trim(), _chDescription.Text.Trim(), AgentSelected(_chProvider2) ?? "",
        _chAgent.Text.Trim(), _chAppId.Text.Trim(),
        _chReplaceSecret.IsOn ? ChannelSecret.Of(_chSecret.Password) : ChannelSecret.Keep,
        _chStreaming.IsOn, _chTts.IsOn, _chTtsVoice.Text.Trim(),
        ChannelText.ParseOpenIds(_chOpenIds.Text), _chEnabled.IsOn);

    private async Task CreateChannelAsync()
    {
        if (AgentSelected(_chWorkspacePicker) is not { } workspaceId) { WarnChannel("请先选择工作区"); return; }
        var create = ReadChannelEdit(workspaceId, "");
        var errors = ChannelText.Validate(create, hasStoredSecret: false);
        if (errors.Count > 0) { ShowNotice(_chNotice, InfoBarSeverity.Warning, "请先修正表单", string.Join(" ", errors)); return; }
        await RunChannelAsync("渠道已创建", "新的渠道按当前表单配置写入；App Secret 只写不读。",
            async () => { await _channels.CreateChannelAsync(create); await LoadChannelsAsync(); });
    }

    private async Task SaveChannelAsync()
    {
        if (_chChannel is not { } channel) { WarnChannel("请先选择渠道"); return; }
        if (AgentSelected(_chWorkspacePicker) is not { } workspaceId) { WarnChannel("请先选择工作区"); return; }
        var edit = ReadChannelEdit(workspaceId, channel.ChannelId) with { Name = _chName.Text.Trim() };
        // The channel already has a name, so an empty one here is only a mistake when editing.
        if (string.IsNullOrWhiteSpace(edit.Name)) edit = edit with { Name = channel.Name };
        var errors = ChannelText.Validate(edit, channel.HasAppSecret);
        if (errors.Count > 0) { ShowNotice(_chNotice, InfoBarSeverity.Warning, "请先修正表单", string.Join(" ", errors)); return; }
        await RunChannelAsync("渠道已保存", _chReplaceSecret.IsOn
                ? "App Secret 已替换为新值。"
                : "未勾选替换，Core 保留了已保存的密钥。",
            async () => { await _channels.SaveChannelAsync(edit); await LoadChannelsAsync(); });
    }

    private async Task DeleteChannelAsync()
    {
        if (_chChannel is not { } channel) { WarnChannel("请先选择渠道"); return; }
        if (AgentSelected(_chWorkspacePicker) is not { } workspaceId) { WarnChannel("请先选择工作区"); return; }
        var confirm = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = "删除渠道",
            Content = $"将删除 {channel.Name}（{channel.ProviderId}）及其凭据与绑定关系。该渠道将不再接收消息。此操作不可撤销。",
            PrimaryButtonText = "删除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        await RunChannelAsync("渠道已删除", "渠道清单与绑定已移除。",
            async () => { await _channels.DeleteChannelAsync(workspaceId, channel.ChannelId); await LoadChannelsAsync(); });
    }

    private void WarnChannel(string message) => ShowNotice(_chNotice, InfoBarSeverity.Warning, "请先选择", message);

    private async Task RunChannelAsync(string title, string message, Func<Task> action)
    {
        SetChannelEnabled(false);
        try
        {
            await action();
            SetChannelEnabled(true);
            ShowNotice(_chNotice, InfoBarSeverity.Success, title, message);
        }
        catch (Exception exception) { ReportChannelFailure(exception); }
    }

    private void ReportChannelFailure(Exception exception)
    {
        App.WriteDiagnostic(exception);
        var unavailable = exception is SettingsUnavailableException;
        var (severity, title, message) = exception switch
        {
            SettingsUnavailableException reason => (InfoBarSeverity.Informational, "Core 未就绪",
                reason.Message + " 该分类仍可浏览；表单已禁用，未写入任何内容。"),
            KeyNotFoundException missing => (InfoBarSeverity.Warning, "目标不存在", missing.Message),
            NotSupportedException unsupported => (InfoBarSeverity.Warning, "该服务商尚未实现", unsupported.Message),
            ArgumentException argument => (InfoBarSeverity.Warning, "请求被 Core 拒绝", argument.Message),
            InvalidOperationException invalid => (InfoBarSeverity.Warning, "Core 拒绝了该操作", invalid.Message),
            _ => (InfoBarSeverity.Error, "操作失败", "请查看诊断日志后重试。")
        };
        SetChannelEnabled(!unavailable);
        ShowNotice(_chNotice, severity, title, message);
        ShowNotice(_chProviderNotice, severity, title, message);
    }

    private void SetChannelEnabled(bool enabled)
    {
        foreach (var control in new Control[]
        {
            _chProviderPicker, _chProviderName, _chProviderDescription, _chProviderEnabled,
            _chWorkspacePicker, _chChannelPicker, _chName, _chDescription, _chProvider2, _chAgent, _chAppId,
            _chReplaceSecret, _chSecret, _chStreaming, _chTts, _chTtsVoice, _chOpenIds, _chEnabled
        }) control.IsEnabled = enabled;
        if (!enabled) { _chSave.IsEnabled = false; _chDelete.IsEnabled = false; return; }
        _chSave.IsEnabled = _chChannel is not null;
        _chDelete.IsEnabled = _chChannel is not null;
        _chSecret.IsEnabled = _chReplaceSecret.IsOn;
    }
}
