using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PuddingChat.WinUI;

public sealed class RoleConfigurationForm(IConfigurationClient client, IWorkspaceSetupClient models, RoleKey role) : UserControl
{
    private RoleSettings? _current;
    private readonly TextBox _name = new() { Header = "角色名称", MaxLength = 80 };
    private readonly TextBox _description = new() { Header = "职责描述", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxLength = 512 };
    private readonly TextBox _role = new() { Header = "角色类型", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 160 };
    private readonly TextBox _prompt = new() { Header = "系统提示词", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 220 };
    private readonly CheckBox _enabled = new() { Content = "启用角色" };
    private readonly ComboBox _model = new() { Header = "聊天模型", DisplayMemberPath = "Label" };
    private readonly InfoBar _notice = new() { IsClosable = false };
    public async Task LoadAsync(CancellationToken ct)
    {
        _current = await client.GetRoleSettingsAsync(role, ct);
        var choices = new List<ModelChoice> { new("", "", "使用默认模型") };
        choices.AddRange(await models.GetSetupModelsAsync(ct));
        if (!string.IsNullOrWhiteSpace(_current.ProviderId) && !choices.Any(m => m.ProviderId == _current.ProviderId && m.ModelId == _current.ModelId))
            choices.Add(new(_current.ProviderId, _current.ModelId ?? "", "当前绑定（不可用，保留或重新选择）"));
        _model.ItemsSource = choices;
        _model.SelectedItem = choices.FirstOrDefault(m => m.ProviderId == _current.ProviderId && m.ModelId == _current.ModelId) ?? choices[0];
        _name.Text = _current.Name; _description.Text = _current.Description; _role.Text = _current.Role;
        _prompt.Text = _current.SystemPrompt; _enabled.IsChecked = _current.Enabled;
        var panel = new StackPanel { Spacing = 12, MinWidth = 380 };
        foreach (var element in new UIElement[] { _name, _description, _enabled, _model, _role, _prompt, _notice }) panel.Children.Add(element);
        Content = new ScrollViewer { Content = panel, MaxHeight = 560 };
    }
    public async Task<bool> SaveAsync(CancellationToken ct)
    {
        if (_current is null || !IsEnabled) return false;
        IsEnabled = false;
        try
        {
            if (string.IsNullOrWhiteSpace(_name.Text)) throw new ArgumentException();
            var model = (ModelChoice)_model.SelectedItem;
            await client.SaveRoleSettingsAsync(_current with { Name = _name.Text.Trim(), Description = _description.Text,
                Enabled = _enabled.IsChecked == true, Role = _role.Text, SystemPrompt = _prompt.Text,
                ProviderId = model.ProviderId, ModelId = model.ModelId }, ct);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch { _notice.IsOpen = true; _notice.Severity = InfoBarSeverity.Error; _notice.Title = "保存未完成"; _notice.Message = "请检查角色名称、模型绑定，或重试。"; return false; }
        finally { IsEnabled = true; }
    }
}
