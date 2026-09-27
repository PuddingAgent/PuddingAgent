using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace PuddingChat.WinUI;

/// <summary>Native selectable text/code blocks and canonical process disclosure; never renders HTML.</summary>
public sealed class MessageCard : UserControl
{
    private Expander? _process;
    private readonly TurnContentView _flow = new();
    private readonly TextBlock _header = new() { FontSize = 12, Opacity = .65, TextWrapping = TextWrapping.Wrap };
    private ChatMessage _message = null!;
    private readonly InfoBar _outcome = new() { IsClosable = false, Severity = InfoBarSeverity.Error };
    private readonly Dictionary<string, ProcessItem> _events = [];
    private string? _run;
    private readonly StackPanel _attachments = new() { Spacing = 8 };
    private readonly IImageAttachmentClient? _imageClient;
    private readonly string? _workspace;
    private readonly CancellationToken _ct;
    private ContentPart[] _parts = [];
    public void Update(ChatMessage message)
    {
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
                    _attachments.Children.Add(new ImageAttachmentView(_imageClient, _workspace, id, $"图片 {number} · 展开预览", _ct));
                else _attachments.Children.Add(new TextBlock { Text = $"附件 {number} · {part.Type}", Opacity = .65 });
            }
        }
        _header.Text = $"{message.SourceName}  ·  {message.CreatedAt.ToLocalTime():HH:mm}  ·  {message.Status}";
        if (_run != message.RunId) { _events.Clear(); _run = message.RunId; }
        foreach (var item in message.ProcessItems) _events[item.Id] = item;
        _flow.Update(_events.Values, message.Content);
        _outcome.IsOpen = message.TurnOutcome?.ErrorMessage is { Length: > 0 };
        _outcome.Title = message.TurnOutcome?.Status ?? "";
        _outcome.Message = message.TurnOutcome?.ErrorMessage ?? "";
        if (_process is not null) _process.Header = "加载完整执行明细";
    }
    public bool IsProcessExpanded { get => _process?.IsExpanded ?? false; set { if (_process is not null) _process.IsExpanded = value; } }
    public MessageCard(ChatMessage message, Func<Task<ProcessDetails>>? loadDetails = null, IImageAttachmentClient? imageClient = null, string? workspace = null, CancellationToken ct = default)
    {
        _imageClient = imageClient; _workspace = workspace; _ct = ct;
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(_header); panel.Children.Add(_flow); panel.Children.Add(_attachments); Update(message);
        panel.Children.Add(_outcome);
        if (message.Role != "user")
        {
            var details = new StackPanel { Spacing = 8 };
            var expander = new Expander { Header = "加载完整执行明细",
                Content = details, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            _process = expander;
            var loaded = false; var loading = false;
            expander.Expanding += async (_, _) =>
            {
                if (loaded || loading) return; loading = true;
                try
                {
                    var result = loadDetails is null ? new ProcessDetails(message.MessageId, message.ProcessItems) : await loadDetails();
                    details.Children.Clear(); foreach (var item in result.ProcessItems) _events[item.Id] = item;
                    _flow.Update(_events.Values, _message.Content);
                    if (result.Window?.HasMoreBefore == true) details.Children.Insert(0, new TextBlock { Text = "当前为部分事件窗口。", Opacity = .6 });
                    loaded = true;
                }
                catch (OperationCanceledException) { }
                catch (Exception) { details.Children.Clear(); details.Children.Add(new TextBlock { Text = "过程明细加载失败，请收起后重试。" }); }
                finally { loading = false; }
            };
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
    public static UIElement RenderText(string text) => new MarkdownView(text);
}
