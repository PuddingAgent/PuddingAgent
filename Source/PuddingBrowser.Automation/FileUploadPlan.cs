using System.Text;
using System.Text.Json;

namespace PuddingBrowser.Automation;

/// <summary>
/// **文件上传的 CDP 请求规划**（设计方案 §7 的真实输入路径）。
///
/// 上传和点击/输入不一样：它不是"往页面发事件"，而是**让浏览器把本地文件交给某个 input 元素**。
/// 驱动必须按三步走，且中间一步的返回值是下一步的输入：
/// <list type="number">
///   <item><c>DOM.getDocument</c> 拿根节点 id；</item>
///   <item><c>DOM.querySelector</c> 用我们写在元素上的 <c>data-pudding-ref</c> 定位目标节点；</item>
///   <item><c>DOM.setFileInputFiles</c> 把文件交给该节点。</item>
/// </list>
///
/// 抽成纯函数的原因：**选择器的转义**（ref 里可能出现引号/反斜杠）与**三步之间的形状约定**
/// 都能在进程内逐条验证；真机上只剩余"浏览器是否接受"这一件事。
/// </summary>
public static class FileUploadPlan
{
    /// <summary>元素上的引用属性（观察时写入；上传时用它反查节点）。</summary>
    public const string RefAttribute = "data-pudding-ref";

    /// <summary>单次上传的文件数上限（防一次调用塞入成百上千个文件）。</summary>
    public const int MaxFiles = 32;

    /// <summary>把引用编成 CSS 选择器（**正确转义**：引号与反斜杠必须转义，否则选择器无效或选错）。</summary>
    public static string SelectorForRef(string elementRef)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(elementRef);

        var builder = new StringBuilder(elementRef.Length + 24);
        builder.Append('[').Append(RefAttribute).Append("=\"");
        foreach (var character in elementRef)
        {
            if (character is '"' or '\\')
            {
                builder.Append('\\');
            }

            builder.Append(character);
        }

        builder.Append("\"]");
        return builder.ToString();
    }

    /// <summary>第 1 步：<c>DOM.getDocument</c> 的参数（只要根节点，不要整棵树）。</summary>
    public static string BuildDocumentParameters() => """{"depth":0}""";

    /// <summary>第 2 步：<c>DOM.querySelector</c> 的参数。</summary>
    public static string BuildQueryParameters(int rootNodeId, string elementRef)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rootNodeId);
        return JsonSerializer.Serialize(new
        {
            nodeId = rootNodeId,
            selector = SelectorForRef(elementRef),
        });
    }

    /// <summary>第 3 步：<c>DOM.setFileInputFiles</c> 的参数（文件路径原样交给浏览器）。</summary>
    public static string BuildSetFilesParameters(int nodeId, IReadOnlyList<string> files)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(nodeId);
        ArgumentNullException.ThrowIfNull(files);

        return JsonSerializer.Serialize(new
        {
            files,
            nodeId,
        });
    }

    /// <summary>校验文件清单（空、相对路径、超量都在**触达浏览器之前**拒绝）。</summary>
    public static bool TryValidateFiles(IReadOnlyList<string>? files, out string? error)
    {
        error = null;

        if (files is not { Count: > 0 })
        {
            error = "upload requires at least one file path";
            return false;
        }

        if (files.Count > MaxFiles)
        {
            error = $"upload accepts at most {MaxFiles} files per call";
            return false;
        }

        foreach (var file in files)
        {
            if (string.IsNullOrWhiteSpace(file))
            {
                error = "upload file paths must not be empty";
                return false;
            }

            // 相对路径的含义取决于浏览器的进程工作目录 —— 那是**别人**的工作目录，
            // 因此这里只接受绝对路径（拒绝比"猜一个基准目录"诚实）。
            if (!Path.IsPathFullyQualified(file))
            {
                error = $"upload requires absolute file paths; '{file}' is relative";
                return false;
            }
        }

        return true;
    }

    /// <summary>从 <c>DOM.getDocument</c> 的返回里取根节点 id（缺字段就失败，不猜 0）。</summary>
    public static bool TryReadRootNodeId(JsonElement response, out int nodeId)
    {
        nodeId = 0;
        return response.TryGetProperty("root", out var root)
            && root.TryGetProperty("nodeId", out var id)
            // ⚠️ TryGetInt32 在类型不是 Number 时会**抛异常**（不是返回 false），因此先判类型：
            // 一个畸形响应必须变成"读不出来"，而不是把异常抛到调用方。
            && id.ValueKind == JsonValueKind.Number
            && id.TryGetInt32(out nodeId);
    }

    /// <summary>从 <c>DOM.querySelector</c> 的返回里取目标节点 id（0 = 没找到，如实返回）。</summary>
    public static bool TryReadNodeId(JsonElement response, out int nodeId)
    {
        nodeId = 0;
        return response.TryGetProperty("nodeId", out var id)
            && id.ValueKind == JsonValueKind.Number
            && id.TryGetInt32(out nodeId);
    }
}
