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
        sb.AppendLine("- `search_grep` is the rg-like content-search tool: use `query` for the regex/text, `directory` for the root, and `pattern` for the file glob. It is deliberately simple and stable, but enumerates at most 2000 files, scans at most 2000 files / 64MB, and declares every limit it hits (it never silently reports \"(no matches)\"). When coverage is partial it also reports `searched_scope`, `complete=false` and `limit_reason` — a partial empty result is NOT proof that something does not exist; narrow the scope or switch backend.");
        sb.AppendLine("- Prefer the indexed tools — `code_symbol_search` / `code_explore` return in milliseconds — only while the index is current. Their hits are validated (file must exist and live under its registered project root); rejected hits are reported as `stale_skipped`, and an unregistered project fails closed with `not_registered`. Whenever you see stale-path or unregistered-index signals, stop trusting the index and run a live `search_grep` in the current repository instead of retrying the index with new keywords.");
        sb.AppendLine("- `shell` returns short-command output directly; use `command` + `working_directory`. On Windows auto means PowerShell, not CMD: CMD syntax requires `shell=\"cmd\"`; use `shell=\"wsl\"` for Unix/Linux. Do not retry a shell syntax mismatch by changing only the trailing command. Prefer `search_grep` for content search because WSL does not guarantee that `rg` is installed.");
        sb.AppendLine("- `terminal_start` briefly waits for short jobs (default `yield_time_ms=1000`, 0 returns immediately). Windows terminal jobs use CMD; PowerShell needs explicit `pwsh -NoProfile -Command`. Put the directory in `cwd`; do not prefix commands with `cd` or `Set-Location`.");
        sb.AppendLine("- `file_patch` and `apply_patch` accept unified diff and Codex `*** Begin Patch` text. Prefer their canonical schemas even though common Harness aliases are normalized at execution.");

        if (available.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Available Tools");
            sb.AppendLine(string.Join(", ", available
                .OrderBy(tool => tool.ToolId, StringComparer.OrdinalIgnoreCase)
                .Select(tool => $"`{tool.ToolId}`")));
            sb.Append("Function schemas are authoritative for descriptions and arguments. To call a tool, set `tool.name` to its id and `tool.args` to the validated arguments.");
            if (HasTool(available, ToolExposurePlanner.SearchToolId))
                sb.Append(" Use `search_tools` to discover deferred tools.");
            sb.AppendLine();
            if (HasTool(available, "terminal_start"))
            {
                sb.AppendLine();
                sb.AppendLine("Terminal command guidance:");
                sb.AppendLine("- After `terminal_start`, inspect job.status: completed jobs already include exit_code/output and need NO wait call. Only Running jobs need `terminal_wait`, with `from_offset=output.next_offset` and `wait_seconds` sized to the expected runtime (builds/tests: 180-600). Do NOT poll repeatedly with short waits; every tool call costs a full model round.");
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
