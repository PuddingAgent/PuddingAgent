using System.Text;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools;

internal static class ToolLoopInstructionBuilder
{
    public static string BuildFromDescriptors(IReadOnlyList<ToolDescriptor> available)
    {
        var sb = new StringBuilder();

        sb.AppendLine("\n\n---");
        sb.AppendLine("## Output Format (STRICT)");
        sb.AppendLine("You MUST output ONLY valid JSON. Do NOT output markdown, prose, or any text outside the JSON object.");
        sb.AppendLine();
        sb.AppendLine("```json");
        sb.AppendLine("{");
        sb.AppendLine("  \"status\": \"CONTINUE | DONE | WAIT | FAILED\",");
        sb.AppendLine("  \"message\": \"the complete current reasoning or final deliverable\",");
        sb.AppendLine("  \"tool\": {");
        sb.AppendLine("    \"name\": \"tool_id or null\",");
        sb.AppendLine("    \"args\": {}");
        sb.AppendLine("  },");
        sb.AppendLine("  \"meta\": { \"reason\": \"optional explanation\" }");
        sb.AppendLine("}");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("1. This JSON object is the Runtime control envelope. Any task-requested output format belongs verbatim inside `message`.");
        sb.AppendLine("2. Task not yet complete -> `status = \"CONTINUE\"`, optionally set `tool`.");
        sb.AppendLine("3. Task is complete -> `status = \"DONE\"`, set `tool` to `null`, and put the COMPLETE requested deliverable in `message`.");
        sb.AppendLine("4. A DONE `message` must never be only a status sentence or a summary that points to content from an earlier round.");
        sb.AppendLine("5. Must wait for external event or approval -> `status = \"WAIT\"`, explain in `meta.reason`.");
        sb.AppendLine("6. Cannot proceed (unrecoverable error) -> `status = \"FAILED\"`, explain in `meta.reason`.");
        sb.AppendLine("7. Output `DONE` ONLY when you are certain everything is finished.");
        sb.AppendLine("8. NEVER output anything outside the JSON object.");

        if (available.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Available Tools");
            sb.AppendLine(string.Join(", ", available
                .OrderBy(tool => tool.ToolId, StringComparer.OrdinalIgnoreCase)
                .Select(tool => $"`{tool.ToolId}`")));
            sb.AppendLine("Function schemas are authoritative for descriptions and arguments. To call a tool, set `tool.name` to its id and `tool.args` to the validated arguments; use `search_tools` to discover deferred tools.");
            if (HasTool(available, "terminal_start"))
            {
                sb.AppendLine();
                sb.AppendLine("Terminal command guidance:");
                sb.AppendLine("- Use `terminal_start`, then `terminal_wait`; continue truncated output with `terminal_read` and the returned offset.");
                sb.AppendLine("- Use `terminal_cancel` to stop the job. Reserve `shell` for short bounded commands.");
            }
            else if (HasTool(available, "shell"))
            {
                sb.AppendLine("Use `shell` only for short, bounded commands.");
            }
        }
        else
        {
            sb.AppendLine("5. No tools are available in this context. Set `tool` to `null` in every response.");
        }

        return sb.ToString();
    }

    private static bool HasTool(IReadOnlyList<ToolDescriptor> available, string toolId) =>
        available.Any(tool => tool.ToolId.Equals(toolId, StringComparison.OrdinalIgnoreCase));
}
