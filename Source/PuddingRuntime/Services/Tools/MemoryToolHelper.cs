using System.Text.Json;
using PuddingRuntime.Services.Skills;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// 共享工具 helper：ADR-029 workspace 注入。
/// </summary>
internal static class MemoryToolHelper
{
    /// <summary>
    /// 序列化 Skill 参数并注入 Runtime WorkspaceId。
    /// 优先使用 request.WorkspaceId，不与 LLM 参数冲突。
    /// </summary>
    public static string SerializeWithWorkspace(SkillInvokeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.WorkspaceId))
            return JsonSerializer.Serialize(request.Parameters);

        var merged = new Dictionary<string, object>();
        foreach (var kv in request.Parameters)
            merged[kv.Key] = kv.Value;
        merged["workspace_id"] = request.WorkspaceId;
        return JsonSerializer.Serialize(merged);
    }
}
