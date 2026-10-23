using ReverseMarkdown;

namespace PuddingHtml;

/// <summary>Converts HTML fragments into Markdown.</summary>
public interface IHtmlToMarkdownConverter
{
    string Convert(string html);
}

/// <summary>HTML-to-Markdown converter backed by the ReverseMarkdown package.</summary>
public sealed class HtmlToMarkdownConverter : IHtmlToMarkdownConverter
{
    private readonly Converter _converter;

    public HtmlToMarkdownConverter()
    {
        var config = new Config
        {
            GithubFlavored = true,
            RemoveComments = true,
            SmartHrefHandling = true,
            UnknownTags = Config.UnknownTagsOption.Bypass,
        };
        config.WhitelistUriSchemes.Add("http");
        config.WhitelistUriSchemes.Add("https");
        config.WhitelistUriSchemes.Add("mailto");

        _converter = new Converter(config);
    }

    public string Convert(string html) => _converter.Convert(html);
}
