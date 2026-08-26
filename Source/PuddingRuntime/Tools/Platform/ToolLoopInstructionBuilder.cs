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
        sb.AppendLine();
        sb.AppendLine("## Pudding Harness Compatibility");
        sb.AppendLine("- `search_grep` is the rg-like content-search tool: use `query` for the regex/text, `directory` for the root, and `pattern` for the file glob.");
        sb.AppendLine("- `shell` is the short-command equivalent of exec_command; use `command` + `working_directory`. Use `shell=\"powershell\"` for Windows semantics (`pwsh` alias), or `shell=\"wsl\"` for a real Unix/Linux environment when WSL is available. Prefer `search_grep` for content search because WSL does not guarantee that `rg` is installed.");
        sb.AppendLine("- `terminal_start` + one bounded `terminal_wait` is the long-command equivalent. Put the directory in `cwd`; do not prefix commands with `cd` or `Set-Location`.");
        sb.AppendLine("- `file_patch` and `apply_patch` accept unified diff and Codex `*** Begin Patch` text. Prefer their canonical schemas even though common Harness aliases are normalized at execution.");

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
                sb.AppendLine("- Start long commands with `terminal_start`, then call `terminal_wait` ONCE with `wait_seconds` sized to the expected runtime (builds/tests: 180-600). It blocks until the job exits — do NOT poll repeatedly with short waits; every tool call costs a full model round.");
                sb.AppendLine("- Continue truncated output with `terminal_read` and the returned offset. Use `terminal_cancel` to stop the job. Reserve `shell` for short bounded commands.");
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
