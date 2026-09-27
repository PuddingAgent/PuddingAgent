using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PuddingChat.WinUI;

/// <summary>Read-only view of one child invocation; never merges child execution into the parent conversation.</summary>
public sealed class SubAgentInspector : UserControl, IDisposable
{
    private readonly ISubAgentInspectionClient _client;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _ct;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly InfoBar _notice = new() { IsOpen = false, IsClosable = false, Severity = InfoBarSeverity.Warning };
    private readonly InfoBar _error = new() { IsOpen = false, IsClosable = false, Severity = InfoBarSeverity.Error };
    private readonly Button _refresh = new() { Content = "刷新归档" };
    private readonly MarkdownView _task = new("");
    private readonly MarkdownView _output = new("");
    private readonly TurnContentView _activities = new();
    private Task? _loading;
    private bool _disposed;
    public SubAgentInspectionKey Key { get; }
    public SubAgentInspection? Snapshot { get; private set; }
    public string LoadError => _error.IsOpen ? _error.Message : "";
    public SubAgentInspector(SubAgentInspectionKey key, ISubAgentInspectionClient client)
    {
        Key = key; _client = client; _ct = _lifetime.Token;
        var panel = new StackPanel { Spacing = 12, Padding = new Thickness(16), MaxWidth = 900 };
        panel.Children.Add(new TextBlock { Text = "子代理执行详情", FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = $"Run · {key.RunId}", IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap, Opacity = .65 });
        panel.Children.Add(_status); panel.Children.Add(_refresh); panel.Children.Add(_error); panel.Children.Add(_notice);
        panel.Children.Add(Section("委派任务", _task, true));
        panel.Children.Add(Section("执行活动", _activities, true));
        panel.Children.Add(Section("完整结果", _output, false));
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _refresh.Click += async (_, _) => await LoadAsync();
        _status.Text = "尚未读取归档";
    }
    private static Expander Section(string title, UIElement content, bool expanded) => new()
    {
        Header = title, Content = content, IsExpanded = expanded,
        HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
    };
    public Task LoadAsync()
    {
        if (_disposed) return Task.CompletedTask;
        if (_loading is { IsCompleted: false }) return _loading;
        return _loading = LoadCoreAsync();
    }
    private async Task LoadCoreAsync()
    {
        _refresh.IsEnabled = false; _refresh.Content = "正在读取…"; _error.IsOpen = false;
        try
        {
            var snapshot = await _client.ReadAsync(Key, _ct).WaitAsync(_ct);
            _ct.ThrowIfCancellationRequested();
            if (snapshot.Key != Key) throw new InvalidOperationException("返回的归档不属于当前角色、会话或运行。");
            Snapshot = snapshot;
            _status.Text = $"{TurnFlow.StatusLabel(snapshot.Status)} · 开始于 {snapshot.StartedAt.ToLocalTime():g}"
                + (snapshot.CompletedAt is { } end ? $" · 结束于 {end.ToLocalTime():g}" : "")
                + (snapshot.Rounds is { } rounds ? $" · {rounds} 轮" : "")
                + (snapshot.ToolCalls is { } calls ? $" · {calls} 次工具调用" : "");
            _task.Update(snapshot.Task); _output.Update(snapshot.Output ?? "尚无归档结果。");
            _activities.Update(snapshot.Activities, "");
            _notice.Message = string.Join("\n", new[] { snapshot.Error, snapshot.ArchiveWarning }.Where(s => !string.IsNullOrWhiteSpace(s)));
            _notice.IsOpen = _notice.Message.Length > 0;
        }
        catch (OperationCanceledException) when (_ct.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!_disposed)
            {
                _error.Message = Snapshot is null ? "无法读取子代理归档，请重试。" : "刷新失败，当前仍显示上次读取的归档。";
                _error.IsOpen = true;
            }
        }
        finally { if (!_disposed) { _refresh.IsEnabled = true; _refresh.Content = "刷新归档"; } }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _refresh.IsEnabled = false; _lifetime.Cancel(); _lifetime.Dispose();
    }
}
