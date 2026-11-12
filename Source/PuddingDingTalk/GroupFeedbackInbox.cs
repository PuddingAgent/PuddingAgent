using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PuddingDingTalk;

/// <summary>One immutable Markdown entry per explicit submission. No caller-supplied file names or executable markup.</summary>
public sealed partial class GroupFeedbackInbox(string directory)
{
    public string? TryRecord(PublicQuestion question, string messageId, IReadOnlyList<string>? images = null)
    {

        var text = question.Text.Trim();
        var match = Submission().Match(text);
        if (!match.Success || HowToReport().IsMatch(text)) return null;
        var kind = match.Groups["bug"].Success ? "BUG反馈" : "功能需求";
        var details = match.Groups["body"].Value.Trim();
        if (details.Length is 0 or > 400 || details.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c)
            == System.Globalization.UnicodeCategory.Format) || SensitiveFormat().IsMatch(details))
            return "请提交一行不超过 400 字符的脱敏描述，去掉链接、联系方式和凭据。";

        var s = question.Source;
        var key = JsonSerializer.SerializeToUtf8Bytes(new[] { s.CorporationId, s.ConversationId, messageId });
        var id = Convert.ToHexStringLower(SHA256.HashData(key));
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, id + ".md");
        var attachments = images?.Distinct().ToArray() ?? [];
        if (attachments.Length > 4 || attachments.Any(x => !ArtifactId().IsMatch(x)))
            throw new InvalidDataException("Unexpected feedback image reference.");
        var refs = string.Concat(attachments.Select(x => $"\n- 附图 `{x}`\n"));
        var entry = $"# {kind}\n\n{s.SentAt:O}\n\n    {details}\n{refs}";
        if (!File.Exists(target))
        {
            var temp = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllText(temp, entry, new UTF8Encoding(false));
                try { File.Move(temp, target); }
                catch (IOException) when (File.Exists(target)) { }
            }
            finally { File.Delete(temp); }
        }
        if (!string.Equals(File.ReadAllText(target), entry, StringComparison.Ordinal))
            return $"此消息已按编号 {id[..12]} 登记，现有条目与本次内容有差异。";
        return $"已登记{kind}，编号 {id[..12]}。" + (attachments.Length > 0 ? $"已关联 {attachments.Length} 张图片。" : "");
    }

    [GeneratedRegex(@"https?://|\S+@\S+|1[3-9]\d{9}|[A-Za-z0-9_+/=-]{28,}|(?:密码|口令|token|secret|api.?key)\s*[:：=]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveFormat();

    [GeneratedRegex("^(?:bug反馈|反馈bug|提交bug|登记bug|功能需求)(?:时|的时候)?[，,\\s]*(?:怎么|如何|能否|可以.{0,6}吗)", RegexOptions.IgnoreCase)]
    private static partial Regex HowToReport();
    [GeneratedRegex(@"\Avision-[0-9a-f]{32}\z")]
    private static partial Regex ArtifactId();

    [GeneratedRegex(@"^(?:@[^\s]{1,50}\s+)?(?:请|麻烦)?(?:帮我)?(?:我想|我要|我来|想)?(?:登记|反馈|记录|报告|提交|提|报)?(?:一下)?(?:一个|一条|一项|个)?(?:软件|MPCAL|产品)?\s*(?:(?<bug>BUG反馈|BUG|缺陷|故障)|功能需求|需求建议|需求|功能建议|建议)[\s:：,，、。]*(?<body>[\s\S]+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Submission();
}
