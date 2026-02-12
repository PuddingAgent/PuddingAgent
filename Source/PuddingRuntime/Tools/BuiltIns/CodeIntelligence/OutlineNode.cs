namespace PuddingRuntime.Services.Tools;

/// <summary>文件结构树节点。</summary>
public sealed class OutlineNode
{
    /// <summary>节点类型：namespace / class / struct / interface / enum / record / method / property / field / event / constructor / indexer / delegate / enum_member</summary>
    public string Kind { get; set; } = "";

    /// <summary>标识符名称（匿名类型时可能为 null）</summary>
    public string? Name { get; set; }

    /// <summary>方法/构造函数的简短签名，如 "(int x, string y)"</summary>
    public string? Signature { get; set; }

    /// <summary>起始行号（1-based）</summary>
    public int Line { get; set; }

    /// <summary>结束行号（1-based）</summary>
    public int EndLine { get; set; }

    /// <summary>访问修饰符缩写：public=+ private=- protected=# internal=~</summary>
    public string? Modifiers { get; set; }

    /// <summary>返回类型（方法/属性/字段/委托）</summary>
    public string? ReturnType { get; set; }

    /// <summary>子节点</summary>
    public List<OutlineNode> Children { get; set; } = [];
}
