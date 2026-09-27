using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Block = Markdig.Syntax.Block;

namespace PuddingChat.WinUI;

/// <summary>Markdown AST to native controls. Message content is never executed as HTML/XAML.</summary>
public sealed class MarkdownView : StackPanel
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables().UseEmphasisExtras(Markdig.Extensions.EmphasisExtras.EmphasisExtraOptions.Strikethrough).UseTaskLists().UseAutoLinks().DisableHtml().Build();
    private readonly List<(string Source, string Kind, UIElement View)> _rendered = [];
    public MarkdownView(string text) { Spacing = 10; Update(text); }
    public void Update(string text)
    {
        var blocks = Markdown.Parse(text, Pipeline);
        var desired = new List<(string Source, string Kind, UIElement View)>();
        foreach (var block in blocks)
        {
            var source = text.Substring(block.Span.Start, block.Span.Length);
            // Reference definitions can change link meaning without changing paragraph source.
            // Keep only blocks whose AST-independent source is safe to reuse across appends.
            var kind = block.GetType().Name;
            var index = desired.Count;
            var reusable = !source.Contains('[') && index < _rendered.Count
                && _rendered[index].Source == source && _rendered[index].Kind == kind;
            if (block is CodeBlock code && index < _rendered.Count && _rendered[index].Kind == kind
                && _rendered[index].View is CodeBlockView existingCode)
            {
                existingCode.Update(code.Lines.ToString(), (code as FencedCodeBlock)?.Info ?? "代码");
                desired.Add((source, kind, existingCode));
            }
            else desired.Add((source, kind, reusable ? _rendered[index].View : RenderBlock(block)));
        }
        foreach (var child in Children.Where(c => !desired.Any(d => ReferenceEquals(d.View, c))).ToArray()) Children.Remove(child);
        for (var i = 0; i < desired.Count; i++)
            if (i >= Children.Count || !ReferenceEquals(Children[i], desired[i].View))
            { Children.Remove(desired[i].View); Children.Insert(i, desired[i].View); }
        _rendered.Clear(); _rendered.AddRange(desired);
    }

    private static StackPanel RenderChildren(ContainerBlock block)
    {
        var panel = new StackPanel { Spacing = 8 };
        foreach (var child in block) panel.Children.Add(RenderBlock(child));
        return panel;
    }

    private static UIElement RenderBlock(Block block)
    {
        switch (block)
        {
            case CodeBlock code:
                return new CodeBlockView(code.Lines.ToString(), (code as FencedCodeBlock)?.Info ?? "代码");
            case HeadingBlock heading:
                var title = Text(heading.Inline);
                title.FontSize = heading.Level switch { 1 => 25, 2 => 22, 3 => 19, _ => 16 };
                title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                return title;
            case ParagraphBlock paragraph: return Text(paragraph.Inline);
            case QuoteBlock quote:
                var surface = Surfaces.Card("SubtleFillColorSecondaryBrush");
                surface.BorderThickness = new Thickness(3, 0, 0, 0); surface.Padding = new Thickness(12, 8, 8, 8);
                surface.Child = RenderChildren(quote); return surface;
            case ListBlock list:
                var rows = new StackPanel { Spacing = 8 };
                var number = int.TryParse(list.OrderedStart, out var start) ? start : 1;
                foreach (var item in list.OfType<ListItemBlock>())
                {
                    var row = new Grid { ColumnSpacing = 8 };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.Children.Add(new TextBlock { Text = list.IsOrdered ? $"{number++}." : "•", FontSize = 14, HorizontalAlignment = HorizontalAlignment.Right });
                    var contents = RenderChildren(item); Grid.SetColumn(contents, 1); row.Children.Add(contents); rows.Children.Add(row);
                }
                return rows;
            case Table table: return RenderTable(table);
            case ThematicBreakBlock:
                var line = Surfaces.Card("DividerStrokeColorDefaultBrush"); line.Height = 1; return line;
            case ContainerBlock container: return RenderChildren(container);
            case LeafBlock leaf: return leaf.Inline is null ? new TextBlock { Text = leaf.Lines.ToString(), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true } : Text(leaf.Inline);
            default: return new TextBlock { Text = block.ToString(), TextWrapping = TextWrapping.Wrap };
        }
    }

    private static UIElement RenderTable(Table table)
    {
        var grid = new Grid();
        var columns = table.OfType<TableRow>().Select(r => r.Count).DefaultIfEmpty(0).Max();
        for (var i = 0; i < columns; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        var rowIndex = 0;
        foreach (var row in table.OfType<TableRow>())
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var column = 0;
            foreach (var cell in row.OfType<TableCell>())
            {
                var border = Surfaces.Card(row.IsHeader ? "SubtleFillColorSecondaryBrush" : "CardBackgroundFillColorDefaultBrush");
                border.Padding = new Thickness(10); border.BorderThickness = new Thickness(.5);
                var contents = RenderChildren(cell);
                foreach (var text in contents.Children.OfType<TextBlock>())
                {
                    if (row.IsHeader) text.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                    if (column < table.ColumnDefinitions.Count)
                        text.TextAlignment = table.ColumnDefinitions[column].Alignment?.ToString() switch
                        { "Right" => TextAlignment.Right, "Center" => TextAlignment.Center, _ => TextAlignment.Left };
                }
                border.Child = contents;
                Grid.SetRow(border, rowIndex); Grid.SetColumn(border, column++); grid.Children.Add(border);
            }
            rowIndex++;
        }
        return new ScrollViewer { Content = grid, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollMode = ScrollMode.Disabled };
    }

    private static TextBlock Text(ContainerInline? input)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 14, LineHeight = 23 };
        if (input is not null) AddInlines(text.Inlines, input);
        return text;
    }
    private static void AddInlines(InlineCollection target, ContainerInline input)
    {
        foreach (var item in input)
        {
            switch (item)
            {
                case LiteralInline literal: target.Add(new Run { Text = literal.Content.ToString() }); break;
                case CodeInline code: target.Add(new Run { Text = code.Content, FontFamily = new FontFamily("Cascadia Mono, Consolas") }); break;
                case LineBreakInline line: target.Add(line.IsHard ? new LineBreak() : new Run { Text = "\n" }); break;
                case TaskList task: target.Add(new Run { Text = task.Checked ? "☑ " : "☐ " }); break;
                case EmphasisInline emphasis:
                    Span span = emphasis.DelimiterChar == '~' ? new Span { TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough }
                        : emphasis.DelimiterCount >= 2 ? new Bold() : new Italic();
                    AddInlines(span.Inlines, emphasis); target.Add(span); break;
                case LinkInline link:
                    var url = link.GetDynamicUrl?.Invoke() ?? link.Url;
                    Span label = !link.IsImage && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http"
                        ? new Hyperlink { NavigateUri = uri } : new Span();
                    if (link.IsImage) label.Inlines.Add(new Run { Text = "图片：" });
                    AddInlines(label.Inlines, link);
                    if (label is not Hyperlink && !string.IsNullOrEmpty(url)) label.Inlines.Add(new Run { Text = $" ({url})" });
                    target.Add(label); break;
                case AutolinkInline auto:
                    if (Uri.TryCreate(auto.Url, UriKind.Absolute, out var address) && address.Scheme is "http" or "https")
                    { var hyperlink = new Hyperlink { NavigateUri = address }; hyperlink.Inlines.Add(new Run { Text = auto.Url }); target.Add(hyperlink); }
                    else target.Add(new Run { Text = auto.Url });
                    break;
                case ContainerInline container: AddInlines(target, container); break;
                default: target.Add(new Run { Text = item.ToString() }); break;
            }
        }
    }
}
