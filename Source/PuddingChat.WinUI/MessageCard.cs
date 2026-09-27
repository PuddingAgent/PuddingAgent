using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace PuddingChat.WinUI;

/// <summary>Native selectable text/code blocks and canonical process disclosure; never renders HTML.</summary>
public sealed class MessageCard : UserControl, IDisposable
{
    private Expander? _process;
    private readonly StackPanel _details = new() { Spacing = 8 };
    private readonly Func<Task<ProcessDetails>>? _loadDetails;
    private readonly Action<string>? _inspectDelegation;
    private CancellationTokenSource? _detailLoad;
    private bool _detailsLoaded;
    private readonly TurnContentView _flow;
    private readonly MessageViewState _state;
    private readonly CancellationTokenSource _viewLifetime;
    private readonly TextBlock _header = new() { FontSize = 12, Opacity = .65, TextWrapping = TextWrapping.Wrap };
    private readonly Button _copy = new() { Content = "复制", HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private readonly TextBlock _copyError = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private ChatMessage _message = null!;
    private readonly InfoBar _outcome = new() { IsClosable = false, Severity = InfoBarSeverity.Error };
    private readonly Dictionary<string, ProcessItem> _events;
    private readonly StackPanel _attachments = new() { Spacing = 8, Visibility = Visibility.Collapsed };
    private readonly IImageAttachmentClient? _imageClient;
    private readonly string? _workspace;
    private readonly CancellationToken _ct;
    private ContentPart[] _parts = [];
    private bool _disposed;
    private readonly SpeechPlaybackButton? _speech;
    private readonly RoleKey? _speechRole;
    public void Update(ChatMessage message)
    {
        if (_disposed) return;
        if (_message is not null && _message.MessageId != message.MessageId)
            throw new ArgumentException("A message card cannot change message identity.", nameof(message));
        if (_message?.Content != message.Content) { _copy.Content = "复制"; _copyError.Visibility = Visibility.Collapsed; }
        _message = message;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_copy, $"复制 {message.SourceName} 的消息");
        if (_speech is not null && _speechRole is not null)
        {
            _speech.Update(new(_speechRole, message.MessageId, message.Content));
            _speech.Visibility = message.Role is ("assistant" or "agent") && message.Status is ("succeeded" or "completed" or "done")
                ? Visibility.Visible : Visibility.Collapsed;
        }
        var parts = message.ContentParts ?? [];
        if (!_parts.SequenceEqual(parts))
        {
            _parts = parts; _attachments.Children.Clear();
            var number = 0;
            foreach (var part in parts.Where(p => p.Type != "text"))
            {
                number++;
                if (part.Type == "image" && part.ArtifactId is { Length: > 0 } id && _imageClient is not null && _workspace is not null)
                    _attachments.Children.Add(new ImageAttachmentView(_imageClient, _workspace, id, $"图片 {number} · 展开预览", _ct,
                        _state.Images.GetValueOrDefault(id), expanded => _state.Images[id] = expanded));
                else _attachments.Children.Add(new TextBlock { Text = $"附件 {number} · {part.Type}", Opacity = .65 });
            }
            _attachments.Visibility = _attachments.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        _header.Text = $"{message.SourceName}  ·  {message.CreatedAt.ToLocalTime():HH:mm}  ·  {TurnFlow.StatusLabel(message.Status)}";
        if (_state.RunId != message.RunId)
        { _detailLoad?.Cancel(); _detailLoad = null; _detailsLoaded = false; _details.Children.Clear();
            if (_process is not null) { _process.IsExpanded = false; _process.Header = "加载执行明细"; }
            _events.Clear(); _state.RunId = message.RunId; _state.Details = null; _state.Expansions.Clear(); _state.DetailsExpanded = false;
            _state.FlowWindow.Reset(); _state.DetailWindow.Reset(); _flow.Update([], ""); }
        foreach (var item in message.ProcessItems) _events[item.Id] = item;
        _flow.Update(_events.Values, message.Content);
        _outcome.IsOpen = message.TurnOutcome is { Status: not "succeeded" };
        _outcome.Visibility = _outcome.IsOpen ? Visibility.Visible : Visibility.Collapsed;
        _outcome.Severity = message.TurnOutcome?.Status == "cancelled" ? InfoBarSeverity.Informational : InfoBarSeverity.Error;
        _outcome.Title = message.TurnOutcome?.Status switch { "cancelled" => "执行已取消", "failed" => "执行失败", _ => message.TurnOutcome?.Status ?? "" };
        _outcome.Message = message.TurnOutcome?.ErrorMessage ?? "";
        if (_process is not null) _process.Visibility = message.Role != "user" || _outcome.IsOpen ? Visibility.Visible : Visibility.Collapsed;
    }
    public bool IsProcessExpanded { get => _process?.IsExpanded ?? false; set { if (_process is not null) _process.IsExpanded = value; } }
    public MessageCard(ChatMessage message, Func<Task<ProcessDetails>>? loadDetails = null, IImageAttachmentClient? imageClient = null, string? workspace = null, CancellationToken ct = default,
        MessageViewState? state = null, Action<string>? inspectDelegation = null, SpeechPlaybackSession? speech = null, RoleKey? speechRole = null)
    {
        _loadDetails = loadDetails;
        _speechRole = speechRole;
        if (speech is not null && speechRole is not null) _speech = new(speech, new(speechRole, message.MessageId, message.Content));
        _inspectDelegation = inspectDelegation;
        _state = state ?? new(); _events = _state.Events;
        _viewLifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _imageClient = imageClient; _workspace = workspace; _ct = _viewLifetime.Token;
        _flow = new(_state.Expansions, _state.FlowWindow) { InspectDelegation = inspectDelegation,
            Images = imageClient is not null && workspace is not null ? new MarkdownImageContext(imageClient, workspace, _ct) : null };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(_header); panel.Children.Add(_flow); panel.Children.Add(_attachments); Update(message);
        panel.Children.Add(_outcome);
        if (loadDetails is not null || message.Role != "user")
        {
            var expander = new Expander { Header = "加载执行明细",
                Content = _details, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            _process = expander;
            expander.Visibility = message.Role != "user" || _outcome.IsOpen ? Visibility.Visible : Visibility.Collapsed;
            expander.Expanding += async (_, _) =>
            {
                _state.DetailsExpanded = true;
                await LoadProcessDetailsAsync();
            };
            expander.Collapsed += (_, _) => _state.DetailsExpanded = false;
            expander.IsExpanded = _state.DetailsExpanded;
            panel.Children.Add(expander);
        }
        var actions = new Grid { ColumnSpacing = 8 };
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _copy.Click += (_, _) => CopyText(Windows.ApplicationModel.DataTransfer.Clipboard.SetContent);
        actions.Children.Add(_copy);
        if (_speech is not null) { Grid.SetColumn(_speech, 1); actions.Children.Add(_speech); }
        panel.Children.Add(actions); panel.Children.Add(_copyError);
        var surface = Surfaces.Card(message.Role == "user" ? "SubtleFillColorSecondaryBrush" : "CardBackgroundFillColorDefaultBrush");
        surface.Padding = new Thickness(20); surface.Margin = new Thickness(0, 0, 0, 12); surface.CornerRadius = new CornerRadius(14); surface.Child = panel;
        if (message.Role == "user") { surface.HorizontalAlignment = HorizontalAlignment.Right; surface.MaxWidth = 680; }
        Content = surface;
    }
    public async Task LoadProcessDetailsAsync()
    {
        if (_disposed || _detailsLoaded || _detailLoad is not null) return;
        using var load = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        _detailLoad = load;
        var message = _message;
        _details.Children.Clear();
        _details.Children.Add(new ProgressRing { IsActive = true, Width = 20, Height = 20, HorizontalAlignment = HorizontalAlignment.Left });
        _details.Children.Add(new TextBlock { Text = "正在读取这条消息的执行明细…", TextWrapping = TextWrapping.Wrap });
        if (_process is not null) _process.Header = "正在加载执行明细…";
        try
        {
            var result = _state.Details ?? (_loadDetails is null
                ? new ProcessDetails(message.MessageId, message.ProcessItems)
                : await _loadDetails().WaitAsync(load.Token));
            load.Token.ThrowIfCancellationRequested();
            if (result.MessageId != message.MessageId) throw new InvalidOperationException("Unexpected process message identity.");
            _state.Details = result;
            _details.Children.Clear();
            if (_message.Role == "user")
            {
                var execution = new TurnContentView(_state.Expansions, _state.DetailWindow) { InspectDelegation = _inspectDelegation, Images = _flow.Images };
                execution.Update(result.ProcessItems, ""); _details.Children.Add(execution);
            }
            else
            {
                // The current snapshot wins over older detail responses for the same event.
                foreach (var item in result.ProcessItems) _events.TryAdd(item.Id, item);
                _flow.Update(_events.Values, _message.Content);
                _details.Children.Add(new TextBlock { Text = result.ProcessItems.Length == 0
                    ? "这条消息没有额外执行记录。" : "执行记录已合并到上方的思考与工具过程；较早记录可继续展开查看。", TextWrapping = TextWrapping.Wrap });
            }
            if (result.Window?.HasMoreBefore == true) _details.Children.Insert(0, new TextBlock { Text = "当前仅包含部分执行记录。", Opacity = .6, TextWrapping = TextWrapping.Wrap });
            if (_process is not null) _process.Header = result.Window?.HasMoreBefore == true
                ? $"执行明细 · {result.ProcessItems.Length} 项（部分）" : $"执行明细 · {result.ProcessItems.Length} 项";
            _detailsLoaded = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (!load.IsCancellationRequested)
            {
                _details.Children.Clear(); _details.Children.Add(new TextBlock { Text = "执行明细加载失败，请重试。", TextWrapping = TextWrapping.Wrap });
                var retry = new Button { Content = "重试加载", HorizontalAlignment = HorizontalAlignment.Left };
                retry.Click += async (_, _) => await LoadProcessDetailsAsync(); _details.Children.Add(retry);
                if (_process is not null) _process.Header = "执行明细 · 加载失败";
            }
        }
        finally { if (ReferenceEquals(_detailLoad, load)) _detailLoad = null; }
    }
    public static UIElement RenderText(string text) => new MarkdownView(text);
    internal void CopyText(Action<Windows.ApplicationModel.DataTransfer.DataPackage> publish)
    {
        if (_disposed) return;
        try
        {
            var data = new Windows.ApplicationModel.DataTransfer.DataPackage(); data.SetText(_message.Content);
            publish(data); _copy.Content = "已复制"; _copyError.Visibility = Visibility.Collapsed;
        }
        catch
        {
            _copy.Content = "重试复制"; _copyError.Text = "暂时无法写入剪贴板，请重试。"; _copyError.Visibility = Visibility.Visible;
        }
    }
    public void Dispose() { if (_disposed) return; _disposed = true; _copy.IsEnabled = false; _speech?.Dispose(); _viewLifetime.Cancel(); _viewLifetime.Dispose(); }
}
