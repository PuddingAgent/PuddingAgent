namespace PuddingMemoryEngine.Data;

/// <summary>
/// 标准记忆书目录。定义已知的 Book 模板和别名映射。
/// 写入记忆时，系统先查注册表找到匹配的 Book，避免重复创建。
/// </summary>
public static class BookRegistry
{
    /// <summary>标准 Book 模板。</summary>
    public sealed record BookTemplate(
        string Id,
        string CanonicalTitle,
        string Description,
        string[] Tags);

    /// <summary>所有标准 Book 模板。</summary>
    public static readonly IReadOnlyList<BookTemplate> StandardBooks = new BookTemplate[]
    {
        new("user-profile",      "用户档案",   "姓名、角色、技能、背景等稳定个人信息",         new[] { "user", "profile" }),
        new("user-preference",   "用户偏好",   "偏好、习惯、风格、沟通方式",                 new[] { "user", "preference" }),
        new("plan-and-tasks",    "计划与任务",  "当前活跃的任务、待办、里程碑",                new[] { "workspace", "tasks" }),
        new("decision-log",      "决策记录",   "架构、产品、技术选型等关键决策",               new[] { "workspace", "decision" }),
        new("dev-progress",      "开发进度",   "各模块实现进展",                             new[] { "workspace", "dev" }),
        new("project-knowledge", "项目知识",   "代码架构、模块关系、设计约束",                new[] { "workspace", "knowledge" }),
        new("lessons-learned",   "经验教训",   "故障、踩坑、复盘、最佳实践",                 new[] { "workspace", "lessons" }),
        new("dev-workflow",      "开发流程",   "编译、测试、部署等标准操作流程",              new[] { "workspace", "workflow" }),
        new("runtime-diary",     "航海日志",   "重要事件和任务进展",                         new[] { "workspace", "diary" }),
        new("handover-index",    "交接索引",   "指向 memo、session、run archive 的轻量索引", new[] { "workspace", "handover" }),
        new("agent-identity",    "Agent身份与信念", "自我认知、行为准则",                    new[] { "global", "identity" }),
        new("agent-fact",        "事实备忘",   "临时性的单条事实记录",                       new[] { "context", "fact" }),
        new("system-design",     "系统设计",   "框架层面的设计原则和约束",                   new[] { "global", "design" }),
        new("preference",        "偏好KV",     "KV 形式的偏好记录",                          new[] { "user", "preference-kv" }),
    };

    /// <summary>
    /// 别名 → 标准 Book ID 映射。
    /// 当用户传入的 title 匹配别名时，路由到对应的标准 Book。
    /// Key 全部小写以支持大小写无关匹配。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> AliasToCanonicalId;

    /// <summary>标准 Book ID 集合（快速查找）。</summary>
    public static readonly IReadOnlySet<string> CanonicalIds;

    /// <summary>标准 Book Title → ID（快速查找）。</summary>
    public static readonly IReadOnlyDictionary<string, string> TitleToCanonicalId;

    static BookRegistry()
    {
        var aliasMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var idSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var titleMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var book in StandardBooks)
        {
            idSet.Add(book.Id);
            titleMap[book.CanonicalTitle] = book.Id;

            // 别名 = 标准 Title + 英文翻译变体
            aliasMap[book.CanonicalTitle.ToLowerInvariant()] = book.Id;
        }

        // 常见英文别名
        aliasMap["user profile"]        = "user-profile";
        aliasMap["user preferences"]    = "user-preference";
        aliasMap["plans and tasks"]     = "plan-and-tasks";
        aliasMap["plans & tasks"]       = "plan-and-tasks";
        aliasMap["decision record"]     = "decision-log";
        aliasMap["dev progress"]        = "dev-progress";
        aliasMap["project knowledge"]   = "project-knowledge";
        aliasMap["lessons learned"]     = "lessons-learned";
        aliasMap["dev workflow"]        = "dev-workflow";
        aliasMap["runtime diary"]       = "runtime-diary";
        aliasMap["handover index"]      = "handover-index";
        aliasMap["agent identity"]      = "agent-identity";
        aliasMap["agent beliefs"]       = "agent-identity";
        aliasMap["fact"]                = "agent-fact";
        aliasMap["facts"]               = "agent-fact";
        aliasMap["system design"]       = "system-design";
        aliasMap["对话摘要"]             = "runtime-diary";
        aliasMap["经验"]                 = "lessons-learned";

        AliasToCanonicalId = aliasMap;
        CanonicalIds = idSet;
        TitleToCanonicalId = titleMap;
    }

    /// <summary>
    /// 尝试将用户传入的 Book Title 路径到标准 Book ID。
    /// 返回 null 表示未匹配任何标准 Book。
    /// </summary>
    public static string? TryResolveCanonicalId(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;

        var key = title.Trim();

        // 精确匹配标准 Title
        if (TitleToCanonicalId.TryGetValue(key, out var id))
            return id;

        // 别名匹配
        if (AliasToCanonicalId.TryGetValue(key.ToLowerInvariant(), out id))
            return id;

        return null;
    }
}
