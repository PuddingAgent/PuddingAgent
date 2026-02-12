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
        sb.AppendLine("  \"message\": \"your reasoning or final answer\",");
        sb.AppendLine("  \"tool\": {");
        sb.AppendLine("    \"name\": \"tool_id or null\",");
        sb.AppendLine("    \"args\": {}");
        sb.AppendLine("  },");
        sb.AppendLine("  \"meta\": { \"reason\": \"optional explanation\" }");
        sb.AppendLine("}");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("1. Task not yet complete -> `status = \"CONTINUE\"`, optionally set `tool`.");
        sb.AppendLine("2. Task is complete -> `status = \"DONE\"`, set `tool` to `null`.");
        sb.AppendLine("3. Must wait for external event or approval -> `status = \"WAIT\"`, explain in `meta.reason`.");
        sb.AppendLine("4. Cannot proceed (unrecoverable error) -> `status = \"FAILED\"`, explain in `meta.reason`.");
        sb.AppendLine("5. Output `DONE` ONLY when you are certain everything is finished.");
        sb.AppendLine("6. NEVER output anything outside the JSON object.");

        if (available.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Available Tools");
            foreach (var tool in available)
            {
                sb.AppendLine($"- **{tool.Name}** (id: `{tool.ToolId}`): {tool.Description}");
                if (tool.Parameters.Properties.Count > 0)
                {
                    var required = tool.Parameters.Required.ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var parameter in tool.Parameters.Properties)
                    {
                        var suffix = required.Contains(parameter.Name) ? " required" : " optional";
                        sb.AppendLine($"  - `{parameter.Name}` ({parameter.Type},{suffix}): {parameter.Description}");
                    }
                }
            }

            sb.AppendLine();
            sb.AppendLine("To call a tool, set `tool.name` to the tool id and `tool.args` to the arguments.");
            if (HasTool(available, "terminal_start"))
            {
                sb.AppendLine();
                sb.AppendLine("Terminal command guidance:");
                sb.AppendLine("- Prefer `terminal_start` for builds, tests, searches, servers, and any command that may run longer than a few seconds.");
                sb.AppendLine("- After `terminal_start`, use `terminal_wait` with `job_id` and `from_offset` to poll incremental output.");
                sb.AppendLine("- If `terminal_wait` returns `truncated=true` or a `handle`, use `terminal_read` with the returned `read_args` to read buffered output slices.");
                sb.AppendLine("- Canceling a wait must not be treated as canceling the job; use `terminal_cancel` when the process should stop.");
                sb.AppendLine("- If a terminal command fails, diagnose its output before retrying. Repeating the same command is OK for intentional restart/retry workflows, but avoid blind identical reruns without a reason or state change.");
                sb.AppendLine("- Use direct `shell` only for short bounded commands when `terminal_start` is unavailable or a one-shot result is explicitly required.");

                sb.AppendLine("Example - start a terminal job:");
                sb.AppendLine("```json");
                sb.AppendLine("{ \"status\": \"CONTINUE\", \"message\": \"Starting tests\", \"tool\": { \"name\": \"terminal_start\", \"args\": { \"command\": \"dotnet test\", \"cwd\": \"/workspace\" } } }");
                sb.AppendLine("```");
                sb.AppendLine("Example - poll terminal output:");
                sb.AppendLine("```json");
                sb.AppendLine("{ \"status\": \"CONTINUE\", \"message\": \"Checking test output\", \"tool\": { \"name\": \"terminal_wait\", \"args\": { \"job_id\": \"abc123\", \"from_offset\": 0, \"wait_seconds\": 3 } } }");
                sb.AppendLine("```");
                if (HasTool(available, "terminal_read"))
                {
                    sb.AppendLine("Example - read truncated terminal output:");
                    sb.AppendLine("```json");
                    sb.AppendLine("{ \"status\": \"CONTINUE\", \"message\": \"Reading more output\", \"tool\": { \"name\": \"terminal_read\", \"args\": { \"job_id\": \"abc123\", \"from_offset\": 120 } } }");
                    sb.AppendLine("```");
                }
            }
            else if (HasTool(available, "shell"))
            {
                sb.AppendLine("Example - run a short shell command:");
                sb.AppendLine("```json");
                sb.AppendLine("{ \"status\": \"CONTINUE\", \"message\": \"Listing files\", \"tool\": { \"name\": \"shell\", \"args\": { \"command\": \"ls -la\" } } }");
                sb.AppendLine("```");
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
