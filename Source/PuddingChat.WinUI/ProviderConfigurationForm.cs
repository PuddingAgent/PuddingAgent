using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PuddingChat.WinUI;

public sealed class ProviderConfigurationForm : UserControl
{
    private readonly IConfigurationClient _client;
    private readonly ComboBox _providers = new() { Header = "已有服务商", DisplayMemberPath = "Name" };
    private readonly ComboBox _models = new() { Header = "已有聊天模型", DisplayMemberPath = "Name" };
    private readonly TextBox _id = new() { Header = "服务商标识" };
    private readonly TextBox _name = new() { Header = "服务商名称" };
    private readonly TextBox _url = new() { Header = "API 地址", PlaceholderText = "https://api.example.com/v1" };
    private readonly CheckBox _enabled = new() { Content = "启用服务商", IsChecked = true };
    private readonly ComboBox _keyChange = new() { Header = "密钥操作", ItemsSource = new[] { "保留现有密钥", "替换密钥", "清除密钥" }, SelectedIndex = 0 };
    private readonly PasswordBox _key = new() { Header = "新 API Key", IsEnabled = false };
    private readonly TextBlock _keyStatus = new();
    private readonly TextBox _modelId = new() { Header = "模型标识" };
    private readonly TextBox _modelName = new() { Header = "模型名称" };
    private readonly ComboBox _protocol = new() { Header = "协议", ItemsSource = new[] { "openai", "responses", "anthropic" }, SelectedIndex = 0 };
    private readonly NumberBox _context = new() { Header = "上下文 token 上限", Value = 32768, Minimum = 1 };
    private readonly NumberBox _output = new() { Header = "输出 token 上限", Value = 4096, Minimum = 1 };
    private readonly InfoBar _notice = new() { IsClosable = false };
    private bool _saving;
    public ProviderConfigurationForm(IConfigurationClient client)
    {
        _client = client;
        var panel = new StackPanel { Spacing = 10, MinWidth = 380 };
        var newProvider = new Button { Content = "新增服务商" };
        newProvider.Click += (_, _) => { _providers.SelectedItem = null; SetProvider(null); };
        var newModel = new Button { Content = "新增模型" };
        newModel.Click += (_, _) => { _models.SelectedItem = null; SetModel(null); };
        foreach (var element in new UIElement[] { _providers, newProvider, _id, _name, _url, _enabled, _keyStatus, _keyChange, _key,
            _models, newModel, _modelId, _modelName, _protocol, _context, _output, _notice }) panel.Children.Add(element);
        Content = new ScrollViewer { Content = panel, MaxHeight = 560 };
        _providers.SelectionChanged += (_, _) => SetProvider(_providers.SelectedItem as ProviderSettings);
        _models.SelectionChanged += (_, _) => SetModel(_models.SelectedItem as ModelSettings);
        _keyChange.SelectionChanged += (_, _) => { _key.IsEnabled = _keyChange.SelectedIndex == 1; if (!_key.IsEnabled) _key.Password = ""; };
        Unloaded += (_, _) => _key.Password = "";
    }
    public async Task LoadAsync(CancellationToken ct)
    {
        _providers.ItemsSource = await _client.GetProvidersAsync(ct);
        _providers.SelectedIndex = -1; SetProvider(null);
    }
    private void SetProvider(ProviderSettings? p)
    {
        _id.Text = p?.Id ?? ""; _id.IsReadOnly = p is not null; _name.Text = p?.Name ?? "";
        _url.Text = p?.BaseUrl ?? ""; _enabled.IsChecked = p?.Enabled ?? true;
        _key.Password = ""; _keyChange.SelectedIndex = 0; _keyStatus.Text = p?.HasKey == true ? "已配置密钥（不回显）" : "尚未配置密钥";
        _models.ItemsSource = p?.Models ?? []; _models.SelectedIndex = p?.Models.Length > 0 ? 0 : -1;
        SetModel(_models.SelectedItem as ModelSettings);
    }
    private void SetModel(ModelSettings? m)
    {
        _modelId.Text = m?.Id ?? ""; _modelId.IsReadOnly = m is not null; _modelName.Text = m?.Name ?? "";
        _protocol.SelectedItem = m?.Protocol ?? "openai"; _context.Value = m?.ContextTokens ?? 32768; _output.Value = m?.OutputTokens ?? 4096;
    }
    public async Task<bool> SaveAsync(CancellationToken ct)
    {
        if (_saving) return false; _saving = true; IsEnabled = false;
        try
        {
            var edit = new ProviderModelEdit(_id.Text.Trim(), _name.Text.Trim(), _url.Text.Trim(), _enabled.IsChecked == true,
                new(_modelId.Text.Trim(), _modelName.Text.Trim(), (string?)_protocol.SelectedItem ?? "", checked((int)_context.Value), checked((int)_output.Value)),
                (SecretChange)_keyChange.SelectedIndex, _key.Password);
            edit.Validate();
            await _client.SaveProviderModelAsync(edit, ct); _key.Password = ""; return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            // Exception text may contain provider material; never echo it in this form.
            _notice.IsOpen = true; _notice.Severity = InfoBarSeverity.Error; _notice.Title = "保存未完成";
            _notice.Message = "请检查标识、地址、协议、token 上限和密钥操作，然后重试。"; return false;
        }
        finally { _saving = false; IsEnabled = true; }
    }
}
