using PuddingCode.Abstractions;
using PuddingCode.Platform;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PuddingMemoryEngine.Services;

/// <summary>
/// 技能评估器：评估单个Skill是否需要改进，以及提取LLM响应中的JSON。
/// 从 SubconsciousOrchestrator 提取，消除上帝类（审计 P0 #5）。
/// </summary>
public sealed class SubconsciousSkillEvaluator
{
    private readonly IMemoryLlmClient _memoryLlmClient;

    public SubconsciousSkillEvaluator(IMemoryLlmClient memoryLlmClient)
    {
        _memoryLlmClient = memoryLlmClient;
    }

    /// <summary>
    /// 评估单个Skill是否需要改进（Flash LLM 分析）。
    /// </summary>
    public async Task<SkillEvaluation?> EvaluateOneSkillAsync(
        AgentSkillEvolutionDocument skill,
        MemoryLlmConfig? config,
        CancellationToken ct)
    {
        var prompt = $@"Evaluate whether this complete Pudding SKILL needs a focused self-improvement.

SKILL ID: {skill.SkillId}
SKILL NAME: {skill.Name}
VERSION: {skill.Version}

CURRENT SKILL.md:
{skill.Markdown}

Check for internally inconsistent steps, missing verification, or clearly obsolete instructions. Do not change a valid skill merely to rephrase it.
Output JSON only: {{""needs_update"":true/false,""reason"":""...""}}";

        var raw = await _memoryLlmClient.ChatWithConfigAsync(
            "You evaluate Pudding skills. Output JSON only.", prompt, config, tools: null, ct: ct);
        var jsonText = ExtractJson(raw ?? "");
        try
        {
            var json = JsonSerializer.Deserialize<JsonElement>(jsonText ?? "{}");
            if (!json.TryGetProperty("needs_update", out var needsUpdate)
                || needsUpdate.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return null;
            }
            return new SkillEvaluation
            {
                SkillId = skill.SkillId, SkillName = skill.Name, CurrentVersion = skill.Version,
                NeedsUpdate = needsUpdate.GetBoolean(),
                Reason = json.TryGetProperty("reason", out var r) ? r.GetString() : null
            };
        }
        catch { return null; }
    }

    /// <summary>
    /// 从LLM原始响应中提取JSON对象。
    /// </summary>
    public static string? ExtractJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var trimmed = raw.Trim();
        if (trimmed.StartsWith("{") && trimmed.EndsWith("}"))
            return trimmed;

        // Try markdown code block first (LLM often wraps JSON in ```json ... ```)
        var markdownJson = Regex.Match(trimmed, "```(?:json)?\\s*(\\{[\\s\\S]*\\})\\s*```", RegexOptions.IgnoreCase);
        if (markdownJson.Success)
            return markdownJson.Groups[1].Value;

        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start >= 0 && end > start)
            return trimmed[start..(end + 1)];

        return null;
    }
}
