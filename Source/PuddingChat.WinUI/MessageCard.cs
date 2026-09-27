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
    private ChatMessage _message = null!;
    private readonly InfoBar _outcome = new() { IsClosable = false, Severity = InfoBarSeverity.Error };
    private readonly Dictionary<string, ProcessItem> _events;
    private readonly StackPanel _attachments = new() { Spacing = 8 };
    private readonly IImageAttachmentClient? _imageClient;
    private readonly string? _workspace;
    private readonly CancellationToken _ct;
    private ContentPart[] _parts = [];
    private bool _disposed;
    public void Update(ChatMessage message)
    {
        if (_disposed) return;
        if (_message is not null && _message.MessageId != message.MessageId)
            throw new ArgumentException("A message card cannot change message identity.", nameof(message));
        _message = message;
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
        }
        _header.Text = $"{message.SourceName}  ·  {message.CreatedAt.ToLocalTime():HH:mm}  ·  {message.Status}";
        if (_state.RunId != message.RunId)
        { _detailLoad?.Cancel(); _detailLoad = null; _detailsLoaded = false; _details.Children.Clear();
            if (_process is not null) _process.IsExpanded = false;
            _events.Clear(); _state.RunId = message.RunId; _state.Details = null; _state.Expansions.Clear(); _state.DetailsExpanded = false;
            _state.FlowWindow.Reset(); _state.DetailWindow.Reset(); _flow.Update([], ""); }
        foreach (var item in message.ProcessItems) _events[item.Id] = item;
        _flow.Update(_events.Values, message.Content);
        _outcome.IsOpen = message.TurnOutcome is { Status: not "succeeded" };
        _outcome.Severity = message.TurnOutcome?.Status == "cancelled" ? InfoBarSeverity.Informational : InfoBarSeverity.Error;
        _outcome.Title = message.TurnOutcome?.Status switch { "cancelled" => "执行已取消", "failed" => "执行失败", _ => message.TurnOutcome?.Status ?? "" };
        _outcome.Message = message.TurnOutcome?.ErrorMessage ?? "";
        if (_process is not null) _process.Visibility = message.Role != "user" || _outcome.IsOpen ? Visibility.Visible : Visibility.Collapsed;
    }
    public bool IsProcessExpanded { get => _process?.IsExpanded ?? false; set { if (_process is not null) _process.IsExpanded = value; } }
    public MessageCard(ChatMessage message, Func<Task<ProcessDetails>>? loadDetails = null, IImageAttachmentClient? imageClient = null, string? workspace = null, CancellationToken ct = default,
        MessageViewState? state = null, Action<string>? inspectDelegation = null)
    {
        _loadDetails = loadDetails;
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
            var expander = new Expander { Header = "加载完整执行明细",
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
        var copy = new Button { Content = "复制", HorizontalAlignment = HorizontalAlignment.Left };
        copy.Click += (_, _) => { var data = new Windows.ApplicationModel.DataTransfer.DataPackage(); data.SetText(_message.Content);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data); };
        panel.Children.Add(copy);
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
            }
            if (result.Window?.HasMoreBefore == true) _details.Children.Insert(0, new TextBlock { Text = "当前为部分事件窗口。", Opacity = .6 });
            _detailsLoaded = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (!load.IsCancellationRequested)
            { _details.Children.Clear(); _details.Children.Add(new TextBlock { Text = "过程明细加载失败，请收起后重试。" }); }
        }
        finally { if (ReferenceEquals(_detailLoad, load)) _detailLoad = null; }
    }
    public static UIElement RenderText(string text) => new MarkdownView(text);
    public void Dispose() { if (_disposed) return; _disposed = true; _viewLifetime.Cancel(); _viewLifetime.Dispose(); }
}
