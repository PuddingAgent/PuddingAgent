using ReverseMarkdown;

namespace PuddingRuntime.Services.Skills;

/// <summary>HTML-to-Markdown converter backed by the ReverseMarkdown package.</summary>
public sealed class ReverseMarkdownHtmlToMarkdownConverter : IHtmlToMarkdownConverter
{
    private readonly Converter _converter;

    public ReverseMarkdownHtmlToMarkdownConverter()
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
