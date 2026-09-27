using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace PuddingChat.WinUI;

public sealed class RoleAvatarCard : UserControl
{
    private readonly TextBlock _status = new() { FontSize = 11, Opacity = .65, TextWrapping = TextWrapping.Wrap };
    public Agent Agent { get; }
    internal event EventHandler? AccessibleStateChanged;
    internal string AccessibleLabel => $"{Agent.Label}，{_status.Text}";
    public RoleAvatarCard(Agent agent, Uri? origin = null)
    {
        Agent = agent;
        var portrait = new PersonPicture { DisplayName = agent.Label, Width = 42, Height = 42, VerticalAlignment = VerticalAlignment.Top };
        // The application adapter supplies a local packaged asset URI, never a credential-bearing URL.
        if (!string.IsNullOrWhiteSpace(agent.AvatarUrl) && Uri.TryCreate(agent.AvatarUrl, UriKind.Absolute, out var image) && image.IsFile)
        {
            var bitmap = new BitmapImage(image);
            bitmap.ImageFailed += (_, _) => portrait.ProfilePicture = null;
            portrait.ProfilePicture = bitmap;
        }
        var labels = new StackPanel { Spacing = 4 };
        labels.Children.Add(new TextBlock { Text = agent.Label, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrWhiteSpace(agent.Description)) labels.Children.Add(new TextBlock
        { Text = agent.Description, FontSize = 12, Opacity = .65, TextWrapping = TextWrapping.Wrap, MaxLines = 2 });
        labels.Children.Add(_status);
        var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(4, 12, 4, 12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(portrait); Grid.SetColumn(labels, 1); grid.Children.Add(labels);
        Content = grid; SetStatus(null);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(this, agent.Description ?? "");
    }
    public void SetStatus(AgentStatus? status)
    {
        var text = Agent.IsFrozen ? "已冻结" : !Agent.IsEnabled ? "已停用"
            : status is null ? "尚无运行状态" : $"{status.Status}  {status.Summary}";
        if (status?.UnreadCount > 0) text += $" · {status.UnreadCount} 条未读";
        if (_status.Text == text) return;
        _status.Text = text;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, AccessibleLabel);
        AccessibleStateChanged?.Invoke(this, EventArgs.Empty);
    }
}
