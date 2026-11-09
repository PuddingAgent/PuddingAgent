namespace PuddingCode.Configuration;

public static class SlackChannelValidation
{
    public static SlackChannelSettings Normalize(SlackChannelSettings settings)
    {
        static string Id(string? value, string prefix, string name)
        {
            value = value?.Trim() ?? "";
            if (value.Length < 2 || prefix.IndexOf(value[0]) < 0
                || !value.All(char.IsAsciiLetterOrDigit)) throw new ArgumentException($"{name} 必须是 Slack API ID（以 {prefix} 开头），不能用显示名称或工作区网址代替");
            return value;
        }
        if (!Path.IsPathFullyQualified(settings.CredentialFile)) throw new ArgumentException("Slack 凭据文件必须使用仓库外的绝对路径");
        var file = Path.GetFullPath(settings.CredentialFile);
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(file)!); directory is not null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) || File.Exists(Path.Combine(directory.FullName, ".git")))
                throw new ArgumentException("Slack 凭据文件不得位于 Git 仓库内");
        return settings with
        {
            TeamId = Id(settings.TeamId, "T", "Team ID"), AppId = Id(settings.AppId, "A", "App ID"), CredentialFile = file,
            AllowedUserIds = (settings.AllowedUserIds ?? []).Select(value => Id(value, "UW", "User ID")).Distinct(StringComparer.Ordinal).ToArray(),
            AllowedBotUserIds = (settings.AllowedBotUserIds ?? []).Select(value => Id(value, "UW", "Bot User ID")).Distinct(StringComparer.Ordinal).ToArray(),
            AllowedChannelIds = (settings.AllowedChannelIds ?? []).Select(value => Id(value, "CG", "Channel ID")).Distinct(StringComparer.Ordinal).ToArray()
        };
    }
}
