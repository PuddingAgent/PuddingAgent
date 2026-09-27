using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PuddingChat.WinUI;

/// <summary>Native first-use form; the host supplies only a BCL application port.</summary>
public sealed class WorkspaceSetupForm : UserControl
{
    private readonly IWorkspaceSetupClient _client;
    private readonly TextBox _id = new() { Header = "工作空间标识", Text = "default", MaxLength = 48 };
    private readonly TextBox _name = new() { Header = "工作空间名称", Text = "我的工作空间", MaxLength = 128 };
    private readonly TextBox _role = new() { Header = "首个角色", Text = "编码助手", MaxLength = 80 };
    private readonly ComboBox _model = new() { Header = "聊天模型", DisplayMemberPath = "Label", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap, Opacity = .7 };
    private readonly InfoBar _error = new() { IsClosable = false, Severity = InfoBarSeverity.Error };
    private bool _busy;
    public WorkspaceSetupForm(IWorkspaceSetupClient client)
    {
        _client = client;
        var panel = new StackPanel { Spacing = 12, MinWidth = 360 };
        panel.Children.Add(new TextBlock { Text = "为你的编码角色准备工作空间", FontSize = 20 });
        panel.Children.Add(_id); panel.Children.Add(_name); panel.Children.Add(_role); panel.Children.Add(_model);
        panel.Children.Add(_hint); panel.Children.Add(_error);
        Content = panel;
    }
    public async Task LoadAsync(CancellationToken ct)
    {
        var models = await _client.GetSetupModelsAsync(ct);
        _model.ItemsSource = models; if (models.Length > 0) _model.SelectedIndex = 0;
        _hint.Text = models.Length == 0
            ? "暂无已配置模型。可以先创建角色；真实对话前，请在模型管理中配置服务商与模型。"
            : "使用已配置的模型。已有工作空间与角色会直接复用，不覆盖配置。";
    }
    public async Task<WorkspaceSetupResult?> SubmitAsync(CancellationToken ct)
    {
        if (_busy) return null;
        _busy = true; IsEnabled = false; _error.IsOpen = false;
        try
        {
            var request = new WorkspaceSetupRequest(_id.Text, _name.Text, _role.Text, _model.SelectedItem as ModelChoice).Normalize();
            return await _client.SetupWorkspaceAsync(request, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            _error.Title = "创建未完成";
            _error.Message = exception is ArgumentException or InvalidOperationException ? exception.Message : "请重试，或在运行中心检查内核。";
            _error.IsOpen = true; return null;
        }
        finally { _busy = false; IsEnabled = true; }
    }
}
