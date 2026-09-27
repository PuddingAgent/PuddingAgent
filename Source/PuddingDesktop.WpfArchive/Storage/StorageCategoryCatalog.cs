namespace PuddingDesktop.Storage;

public sealed class StorageCategoryCatalog
{
    private static readonly IReadOnlyList<StorageCategoryDefinition> OrderedDefinitions =
    [
        new(StorageCategoryKind.Logs, "日志", "运行日志和结构化诊断记录。", "\uE9D9", 0, CanClean: true),
        new(StorageCategoryKind.DatabaseAndIndex, "数据库与索引", "会话数据库、事件存储和全文索引。", "\uE8F1", 1),
        new(StorageCategoryKind.ConversationAndMemory, "会话、Agent 与记忆", "会话、Agent、Workspace 和长期记忆数据。", "\uE8BD", 2),
        new(StorageCategoryKind.AssetsAndDownloads, "附件与下载", "附件、下载、截图和浏览器 Trace。", "\uE896", 3),
        new(StorageCategoryKind.Browser, "Browser 数据", "隔离的 WebView2 用户数据和站点状态。", "\uE774", 4),
        new(StorageCategoryKind.Backups, "备份", "用户数据和配置的备份。", "\uE81C", 5),
        new(StorageCategoryKind.Configuration, "配置", "系统、Agent 模板和 Channel 配置。", "\uE713", 6),
        new(StorageCategoryKind.UnexpectedBuildOutput, "异常开发产物", "不应位于 DataRoot 的构建或测试输出。", "\uE7BA", 7),
        new(StorageCategoryKind.Temporary, "临时数据", "运行时临时文件；当前版本只统计。", "\uE74D", 8),
        new(StorageCategoryKind.Other, "其他", "未归入以上分类的数据。", "\uE8B7", 9),
    ];

    public IReadOnlyList<StorageCategoryDefinition> Definitions => OrderedDefinitions;

    public StorageCategoryDefinition Classify(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return Get(StorageCategoryKind.Other);

        var root = segments[0];
        if (EqualsSegment(root, "logs"))
            return Get(StorageCategoryKind.Logs);

        if (EqualsSegment(root, "databases") || EqualsSegment(root, "fulltext-index"))
            return Get(StorageCategoryKind.DatabaseAndIndex);

        if (EqualsSegment(root, "sessions")
            || EqualsSegment(root, "agents")
            || EqualsSegment(root, "memory")
            || EqualsSegment(root, "workspaces"))
        {
            return Get(StorageCategoryKind.ConversationAndMemory);
        }

        if (EqualsSegment(root, "assets") || IsBrowserArtifact(segments))
            return Get(StorageCategoryKind.AssetsAndDownloads);

        if (EqualsSegment(root, "browser") || IsChannelWebView2Runtime(segments))
            return Get(StorageCategoryKind.Browser);

        if (EqualsSegment(root, "backups"))
            return Get(StorageCategoryKind.Backups);

        if (EqualsSegment(root, "build-validation")
            || EqualsSegment(root, "pudding-build")
            || EqualsSegment(root, "codex-test-results"))
        {
            return Get(StorageCategoryKind.UnexpectedBuildOutput);
        }

        if (EqualsSegment(root, "temp") || EqualsSegment(root, "tmp"))
            return Get(StorageCategoryKind.Temporary);

        if (EqualsSegment(root, "config")
            || EqualsSegment(root, "agent-templates")
            || EqualsSegment(root, "channels"))
        {
            return Get(StorageCategoryKind.Configuration);
        }

        return Get(StorageCategoryKind.Other);
    }

    private StorageCategoryDefinition Get(StorageCategoryKind kind)
        => OrderedDefinitions.Single(item => item.Kind == kind);

    private static bool IsBrowserArtifact(IReadOnlyList<string> segments)
        => segments.Count >= 2
            && EqualsSegment(segments[0], "browser")
            && (EqualsSegment(segments[1], "downloads")
                || EqualsSegment(segments[1], "screenshots")
                || EqualsSegment(segments[1], "traces"));

    private static bool IsChannelWebView2Runtime(IReadOnlyList<string> segments)
        => segments.Count >= 4
            && EqualsSegment(segments[0], "channels")
            && EqualsSegment(segments[2], "runtime")
            && EqualsSegment(segments[3], "webview2");

    private static bool EqualsSegment(string value, string expected)
        => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
}
