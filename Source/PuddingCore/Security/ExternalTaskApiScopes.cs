namespace PuddingCode.Security;

/// <summary>
/// External API v1 scope 白名单。ADR-075 冻结 tasks.*；ADR-082 增加
/// workspace/Agent 目录和消息接入权限。
/// 不存在 "*" 超级 scope；tasks.write 不隐含 tasks.command；tasks.evaluate 不隐含状态变更。
/// 未知 scope 在 Token 创建时 422，运行时 fail closed。
/// </summary>
public static class ExternalTaskApiScopes
{
    public const string TasksRead = "tasks.read";
    public const string TasksWrite = "tasks.write";
    public const string TasksComment = "tasks.comment";
    public const string TasksEvaluate = "tasks.evaluate";
    public const string TasksCommand = "tasks.command";
    public const string WorkspacesRead = "workspaces.read";
    public const string AgentsRead = "agents.read";
    public const string MessagesSend = "messages.send";

    public static readonly IReadOnlyList<string> All =
    [
        TasksRead,
        TasksWrite,
        TasksComment,
        TasksEvaluate,
        TasksCommand,
        WorkspacesRead,
        AgentsRead,
        MessagesSend,
    ];

    private static readonly IReadOnlySet<string> ValidScopes =
        new HashSet<string>(All, StringComparer.Ordinal);

    public static bool IsValid(string scope)
        => ValidScopes.Contains(scope);
}
