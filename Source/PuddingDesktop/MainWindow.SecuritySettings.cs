using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PuddingDesktop.Foundation;

namespace PuddingDesktop;

/// <summary>
/// DS-10 vault and classifier-health slice: manage secret metadata and copy the reference placeholder, and
/// read the classifier health snapshot Core actually has (or its explicit unknown state).
/// </summary>
public sealed partial class MainWindow
{
    private IReadOnlyList<VaultSecret> _secSecrets = [];
    private VaultSecret? _secSecret;
    private ClassifierHealthReport _secHealth = ClassifierHealthReport.NotConfigured;
    private bool _secBuilt;
    private bool _secSwitching;

    private ComboBox _secPicker = null!, _secCategory = null!;
    private TextBox _secName = null!, _secDescription = null!, _secTags = null!;
    private PasswordBox _secValue = null!;
    private TextBlock _secDetail = null!, _secPlaceholder = null!, _secHealthSummary = null!;
    private StackPanel _secClassifiers = null!;
    private Button _secDelete = null!;
    private InfoBar _secNotice = null!;
    private InfoBar _secAuditNotice = null!;

    private void BuildSecurityPanel()
    {
        if (_secBuilt) return;
        _secBuilt = true;

        _secPicker = new ComboBox { Header = "密钥", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_secPicker, "选择密钥");
        _secPicker.SelectionChanged += (_, _) => { if (!_secSwitching) ApplySecretSelection(); };
        var refresh = new Button { Content = "刷新" };
        refresh.Click += async (_, _) => await LoadSecretsAsync();
        var create = new Button { Content = "新建密钥" };
        create.Click += (_, _) => StartNewSecret();
        _secName = Field("名称", "字母、数字、'.'、'_'、'-'");
        _secDescription = Field("描述");
        _secCategory = new ComboBox { Header = "分类", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var category in SecurityText.VaultCategories)
            _secCategory.Items.Add(new ComboBoxItem { Content = SecurityText.DescribeVaultCategory(category), Tag = category });
        _secCategory.SelectedIndex = 0;
        _secTags = Field("标签", "逗号或空格分隔");
        _secValue = new PasswordBox
        {
            Header = "密钥值（只写）", PasswordChar = "●", HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(_secValue, "密钥值");
        var save = new Button { Content = "保存密钥" };
        save.Click += async (_, _) => await SaveSecretAsync();
        _secDelete = new Button { Content = "删除密钥" };
        _secDelete.Click += async (_, _) => await DeleteSecretAsync();
        _secDetail = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 12 };
        _secPlaceholder = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        var copy = new Button { Content = "复制引用占位符" };
        copy.Click += (_, _) => CopySecretPlaceholder();
        _secNotice = new InfoBar { IsOpen = false, IsClosable = true };

        SecurityVaultSettings.Content = new StackPanel
        {
            Spacing = 14,
            Margin = new Thickness(0, 16, 8, 16),
            Children =
            {
                Card("密钥保管库", SecurityText.WriteOnlyNotice + " " + SecurityText.PlaceholderNotice,
                    _secPicker, Row(refresh, create), _secName, _secDescription, _secCategory, _secTags, _secValue,
                    _secDetail, _secPlaceholder, Row(save, copy, _secDelete),
                    _secNotice)
            }
        };

        _secHealthSummary = new TextBlock { TextWrapping = TextWrapping.Wrap };
        _secClassifiers = new StackPanel { Spacing = 2 };
        var reloadHealth = new Button { Content = "刷新健康快照" };
        reloadHealth.Click += async (_, _) => await LoadClassifierHealthAsync();
        _secAuditNotice = new InfoBar { IsOpen = false, IsClosable = true };

        SecurityAuditSettings.Content = new StackPanel
        {
            Spacing = 14,
            Margin = new Thickness(0, 16, 8, 16),
            Children =
            {
                Card("分类器健康", SecurityText.ClassifierUnknownNotice + " 这里是 Core 的进程内快照，不是推断值。",
                    Row(reloadHealth), _secHealthSummary, _secClassifiers,
                    _secAuditNotice)
            }
        };
        SetSecurityEnabled(false);
    }

    internal async Task LoadSecretsAsync()
    {
        if (!_secBuilt) return;
        try
        {
            _secSecrets = await _security.ListSecretsAsync();
            _secSwitching = true;
            try
            {
                var previous = AgentSelected(_secPicker);
                _secPicker.Items.Clear();
                foreach (var secret in _secSecrets)
                    _secPicker.Items.Add(new ComboBoxItem
                    {
                        Content = $"{secret.Name} · {secret.CategoryText}", Tag = secret.KeyVaultId
                    });
                var index = _secSecrets.ToList().FindIndex(secret => secret.KeyVaultId == previous);
                _secPicker.SelectedIndex = _secSecrets.Count > 0 ? Math.Max(0, index) : -1;
            }
            finally { _secSwitching = false; }
            ApplySecretSelection();
            SetSecurityEnabled(true);
            _secNotice.IsOpen = false;
        }
        catch (Exception exception) { ReportSecurityFailure(exception); }
    }

    private void ApplySecretSelection()
    {
        _secSecret = AgentSelected(_secPicker) is { } id
            ? _secSecrets.FirstOrDefault(secret => secret.KeyVaultId == id)
            : null;
        var secret = _secSecret;
        _secName.Text = secret?.Name ?? "";
        _secDescription.Text = secret?.Description ?? "";
        _secTags.Text = SecurityText.FormatTags(secret?.Tags);
        _secValue.Password = "";
        _secCategory.SelectedIndex = Math.Max(0, SecurityText.VaultCategories.ToList()
            .FindIndex(category => string.Equals(category, secret?.Category, StringComparison.OrdinalIgnoreCase)));
        _secDetail.Text = secret is null
            ? (_secSecrets.Count == 0 ? "还没有密钥；可以新建一个。" : "请选择一个密钥。")
            : $"ID {secret.KeyVaultId} · {secret.CategoryText} · {secret.TagsText}\n" +
              $"创建 {secret.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm}" +
              (secret.UpdatedAt is null ? "" : $" · 更新 {secret.UpdatedAt.Value.ToLocalTime():yyyy-MM-dd HH:mm}") + "\n" +
              SecurityText.WriteOnlyNotice;
        _secPlaceholder.Text = secret is null ? "（新建后可复制占位符）" : $"占位符：{secret.Placeholder}";
        _secDelete.IsEnabled = secret is not null;
        _secValue.PlaceholderText = secret is null ? "新建必须填写" : "留空表示保持原密钥";
    }

    private void StartNewSecret()
    {
        _secSwitching = true;
        try { _secPicker.SelectedIndex = -1; }
        finally { _secSwitching = false; }
        _secSecret = null;
        ApplySecretSelection();
        ShowNotice(_secNotice, InfoBarSeverity.Informational, "新建密钥",
            "填写名称、分类与密钥值后保存；密钥值不会被回显。");
    }

    private async Task SaveSecretAsync()
    {
        var edit = new VaultSecretEdit(_secSecret?.KeyVaultId ?? "", _secName.Text.Trim(), _secDescription.Text.Trim(),
            AgentSelected(_secCategory) ?? "general", _secValue.Password, SecurityText.ParseTags(_secTags.Text));
        var errors = SecurityText.Validate(edit);
        if (errors.Count > 0) { ShowNotice(_secNotice, InfoBarSeverity.Warning, "请先修正表单", string.Join(" ", errors)); return; }

        await RunSecurityAsync(edit.IsCreate ? "密钥已创建" : "密钥已保存",
            edit.IsCreate ? "密钥值已加密保存；界面不回显它。" : "元数据已更新；留空的密钥值保持原样。",
            async () =>
            {
                await _security.SaveSecretAsync(edit);
                _secValue.Password = "";
                await LoadSecretsAsync();
            });
    }

    private async Task DeleteSecretAsync()
    {
        if (_secSecret is not { } secret) { WarnSecurity("请先选择密钥"); return; }
        var confirm = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = "删除密钥",
            Content = $"将删除 {secret.Name}（{secret.KeyVaultId}）。使用 {secret.Placeholder} 的配置在注入时会失败。" +
                      "此操作不可撤销。",
            PrimaryButtonText = "删除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        await RunSecurityAsync("密钥已删除", "引用它的配置将无法再注入该密钥。",
            async () => { await _security.DeleteSecretAsync(secret.KeyVaultId); await LoadSecretsAsync(); });
    }

    private void CopySecretPlaceholder()
    {
        if (_secSecret is not { } secret) { WarnSecurity("请先选择密钥"); return; }
        try
        {
            var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
            data.SetText(secret.Placeholder);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
            ShowNotice(_secNotice, InfoBarSeverity.Success, "占位符已复制", secret.Placeholder);
        }
        catch (Exception exception)
        {
            App.WriteDiagnostic(exception);
            ShowNotice(_secNotice, InfoBarSeverity.Warning, "复制失败", "请手动复制占位符：" + secret.Placeholder);
        }
    }

    internal async Task LoadClassifierHealthAsync()
    {
        if (!_secBuilt) return;
        try
        {
            _secHealth = await _security.ReadClassifierHealthAsync();
            _secHealthSummary.Text = SecurityText.DescribeHealthSummary(_secHealth);
            _secClassifiers.Children.Clear();
            if (!_secHealth.Configured)
            {
                _secClassifiers.Children.Add(Muted(SecurityText.ClassifierUnknownNotice));
            }
            else if (_secHealth.Classifiers.Count == 0)
            {
                _secClassifiers.Children.Add(Muted("Core 已接线但没有返回任何分类器快照。"));
            }
            else
            {
                foreach (var status in _secHealth.Classifiers)
                    _secClassifiers.Children.Add(Muted(
                        $"{status.ClassifierId} · {status.HealthText} · 连续失败 {status.ConsecutiveFailures} · " +
                        $"{status.LatencyText} · {status.CheckedText}" +
                        (status.Detail.Length == 0 ? "" : $" · {status.Detail}")));
            }
            _secAuditNotice.IsOpen = false;
        }
        catch (Exception exception) { ReportSecurityFailure(exception); }
    }

    private void WarnSecurity(string message) => ShowNotice(_secNotice, InfoBarSeverity.Warning, "请先选择", message);

    private async Task RunSecurityAsync(string title, string message, Func<Task> action)
    {
        SetSecurityEnabled(false);
        try
        {
            await action();
            SetSecurityEnabled(true);
            ShowNotice(_secNotice, InfoBarSeverity.Success, title, message);
        }
        catch (Exception exception) { ReportSecurityFailure(exception); }
    }

    private void ReportSecurityFailure(Exception exception)
    {
        App.WriteDiagnostic(exception);
        var unavailable = exception is SettingsUnavailableException;
        var (severity, title, message) = exception switch
        {
            SettingsUnavailableException reason => (InfoBarSeverity.Informational, "Core 未就绪",
                reason.Message + " 该分类仍可浏览；表单已禁用，未写入任何内容。"),
            ArgumentException argument => (InfoBarSeverity.Warning, "请求被 Core 拒绝", argument.Message),
            InvalidOperationException invalid => (InfoBarSeverity.Warning, "Core 拒绝了该操作", invalid.Message),
            _ => (InfoBarSeverity.Error, "操作失败", "请查看诊断日志后重试。")
        };
        SetSecurityEnabled(!unavailable);
        ShowNotice(_secNotice, severity, title, message);
        ShowNotice(_secAuditNotice, severity, title, message);
    }

    private void SetSecurityEnabled(bool enabled)
    {
        foreach (var control in new Control[]
        {
            _secPicker, _secName, _secDescription, _secCategory, _secTags, _secValue
        }) control.IsEnabled = enabled;
        _secDelete.IsEnabled = enabled && _secSecret is not null;
    }
}
