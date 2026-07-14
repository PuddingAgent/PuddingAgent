using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools;

[Tool(
    id: "file_patch",
    name: "Patch file",
    description: "Patch text files in the host workspace. Supports single/batch replacements, line-based operations (insert, delete, replace_lines), and regex replacements. Whitespace-insensitive matching by default.",
    category: ToolCategory.FileSystem,
    permission: ToolPermissionLevel.High,
    safety: ToolSafetyFlags.RequiresFileWrite | ToolSafetyFlags.Destructive,
    SortOrder = 45)]
public sealed class FilePatchTool : PuddingToolBase<FilePatchArgs>
{
    private readonly PuddingDataPaths _dataPaths;
    private readonly AuditLogger _audit;
    private readonly ILogger<FilePatchTool> _logger;

    public FilePatchTool()
        : this(CreateDefaultDataPaths(), new AuditLogger(CreateDefaultDataPaths()), NullLogger<FilePatchTool>.Instance)
    {
    }

    public FilePatchTool(ILogger<FilePatchTool> logger)
        : this(CreateDefaultDataPaths(), new AuditLogger(CreateDefaultDataPaths()), logger)
    {
    }

    public FilePatchTool(PuddingDataPaths dataPaths, AuditLogger audit, ILogger<FilePatchTool> logger)
    {
        _dataPaths = dataPaths;
        _audit = audit;
        _logger = logger;
    }

    protected override Task<ToolExecutionResult> ExecuteCoreAsync(
        FilePatchArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(args.PatchText))
            return Task.FromResult(ApplyUnifiedDiffPatch(args, context));

        var patches = ResolvePatches(args).ToArray();
        if (patches.Length == 0)
            return Task.FromResult(ToolExecutionResult.Fail("At least one patch with operations is required."));

        var summaries = new List<string>();
        foreach (var patch in patches)
        {
            if (!HostFileToolPaths.TryResolveInsideWorkspace(patch.Path, out var fullPath, out var resolveError, skipWorkspaceCheck: context.IsYoloMode))
            {
                _audit.Write(OperationZone.External, "file_patch", context.AgentInstanceId,
                    patch.Path, args.Reason, false, 0, context.Trace);
                return Task.FromResult(ToolExecutionResult.Fail(resolveError));
            }

            if (!File.Exists(fullPath))
            {
                var zone = OperationZoneClassifier.ClassifyPath(
                    fullPath, _dataPaths, context.WorkspaceId, context.AgentInstanceId);
                _audit.Write(zone, "file_patch", context.AgentInstanceId,
                    patch.Path, args.Reason, false, 0, context.Trace);
                return Task.FromResult(ToolExecutionResult.Fail($"File not found: {patch.Path}"));
            }

            var zone2 = OperationZoneClassifier.ClassifyPath(
                fullPath, _dataPaths, context.WorkspaceId, context.AgentInstanceId);

            if (zone2 == OperationZone.AgentPrivate && string.IsNullOrWhiteSpace(args.Reason))
            {
                _audit.Write(zone2, "file_patch", context.AgentInstanceId,
                    patch.Path, args.Reason, false, 0, context.Trace);
                return Task.FromResult(ToolExecutionResult.Fail(
                    "Patching agent private files requires a 'reason' parameter. Please explain the purpose of this patch."));
            }

            var sw = Stopwatch.StartNew();
            try
            {
                var original = File.ReadAllText(fullPath, Encoding.UTF8);
                var replacementCount = 0;
                var errors = new List<string>();
                var relPath = Path.GetRelativePath(HostFileToolPaths.WorkspaceRoot, fullPath);

                var ops = patch.Operations ?? [];

                var scopeStartLine = args.ScopeStartLine;
                var scopeEndLine = args.ScopeEndLine;

            foreach (var op in ops)
            {
                var opType = (op.Type ?? "replace").Trim();
                if (opType.Equals("replace", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrEmpty(op.OldText))
                    {
                        return Task.FromResult(ToolExecutionResult.Fail(
                            $"replace operation in {relPath} requires 'old_text'. " +
                            "Provide the exact text to find before replacing. " +
                            "Example: operations=[{type='replace', old_text='old code', new_text='new code'}]"));
                    }
                }
                if (opType.Contains("regex", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrEmpty(op.Pattern))
                    {
                        return Task.FromResult(ToolExecutionResult.Fail(
                            $"regexReplace operation in {relPath} requires 'pattern'. " +
                            "Provide the regex pattern to match. " +
                            "Example: operations=[{type='regexReplace', pattern='Console.WriteLine', replacement='logger.Log'}]"));
                    }
                }
            }

            var replacements = CollectReplacements(original, ops, ref replacementCount, errors, relPath, scopeStartLine, scopeEndLine);
                var current = ApplyReplacements(original, replacements);

                foreach (var op in ops)
                {
                    var opType = (op.Type ?? "replace").Trim();
                    if (!opType.Contains("regex", StringComparison.OrdinalIgnoreCase)) continue;
                    current = ApplyRegexOperation(current, op, ref replacementCount);
                }

                current = ApplyLineOperations(current, ops, ref replacementCount, errors, relPath);

                var isDryRun = args.DryRun != false;
                var previewPrefix = isDryRun ? "(preview - set dry_run=false to apply)" : "";
                if (current == original)
                {
                    var msg = $"{relPath}: unchanged {previewPrefix}".TrimEnd();
                    if (errors.Count > 0)
                        msg += $"\n  {errors.Count} issue(s):\n    " + string.Join("\n    ", errors);
                    summaries.Add(msg);
                    continue;
                }

                var diff = GenerateSimpleDiff(original, current);
                if (isDryRun)
                {
                    summaries.Add($"{relPath}: (preview - set dry_run=false to apply)\n{diff}");
                    continue;
                }

                File.WriteAllText(fullPath, current, Encoding.UTF8);
                var successMsg = $"{relPath}: patched ({replacementCount} replacements)";
                if (errors.Count > 0)
                    successMsg += $"\n  {errors.Count} issue(s):\n    " + string.Join("\n    ", errors);
                if (!string.IsNullOrWhiteSpace(diff))
                    successMsg += $"\n{diff}";
                summaries.Add(successMsg);
                _logger.LogInformation("[FilePatchTool] path={Path} replacements={Replacements}", fullPath, replacementCount);
                _audit.Write(zone2, "file_patch", context.AgentInstanceId,
                    patch.Path, args.Reason, true, sw.ElapsedMilliseconds, context.Trace);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[FilePatchTool] failed path={Path}", fullPath);
                _audit.Write(zone2, "file_patch", context.AgentInstanceId,
                    patch.Path, args.Reason, false, sw.ElapsedMilliseconds, context.Trace);
                return Task.FromResult(ToolExecutionResult.Fail($"Failed to patch file '{patch.Path}': {ex.Message}"));
            }
        }

        return Task.FromResult(ToolExecutionResult.Ok(string.Join(Environment.NewLine, summaries)));
    }

    private ToolExecutionResult ApplyUnifiedDiffPatch(FilePatchArgs args, ToolExecutionContext context)
    {
        return UnifiedDiffPatchRunner.Apply(
            args.PatchText!,
            args.Reason,
            args.DryRun,
            context,
            _dataPaths,
            _audit,
            "file_patch");
    }

    private static PuddingDataPaths CreateDefaultDataPaths() =>
        PuddingDataPaths.FromRoot(Path.Combine(HostFileToolPaths.WorkspaceRoot, "temp", "tool-audit-data"));

    private static IEnumerable<FilePatchItem> ResolvePatches(FilePatchArgs args)
    {
        if (args.Patches is { Count: > 0 })
            return args.Patches;

        if (!string.IsNullOrWhiteSpace(args.Path) && args.Operations is { Count: > 0 })
            return [new FilePatchItem { Path = args.Path, Operations = args.Operations }];

        return [];
    }

    private static List<(int Index, int Length, string NewText)> CollectReplacements(
        string original, IReadOnlyList<FilePatchOperation> ops, ref int replacementCount,
        List<string> errors, string relPath, int? scopeStartLine, int? scopeEndLine)
    {
        var matches = new List<(int Index, int Length, string NewText)>();
        var usedRanges = new SortedSet<(int Start, int End)>();

        int scopeStart = 0, scopeEnd = original.Length;
        if (scopeStartLine.HasValue || scopeEndLine.HasValue)
        {
            var slines = original.Replace("\r\n", "\n").Split('\n');
            if (scopeStartLine.HasValue && scopeStartLine.Value > 0)
                scopeStart = slines.Take(scopeStartLine.Value - 1).Sum(l => l.Length + 1);
            if (scopeEndLine.HasValue && scopeEndLine.Value <= slines.Length)
                scopeEnd = slines.Take(scopeEndLine.Value).Sum(l => l.Length + 1);
        }

        foreach (var op in ops)
        {
            var type = (op.Type ?? "replace").Trim();
            if (type.Contains("regex", StringComparison.OrdinalIgnoreCase)) continue;
            if (!type.Equals("replace", StringComparison.OrdinalIgnoreCase)) continue;

            var oldText = op.OldText ?? "";
            var newText = op.NewText ?? "";
            if (string.IsNullOrEmpty(oldText))
            {
                errors.Add("replace operation requires 'old_text' - skipped.");
                continue;
            }

            var candidates = FindReplacementCandidates(original, oldText)
                .Where(match => match.Index >= scopeStart && match.Index + match.Length <= scopeEnd)
                .Where(match => !usedRanges.Any(r => match.Index < r.End && match.Index + match.Length > r.Start))
                .ToList();

            if (candidates.Count == 0)
            {
                var snippet = oldText.Length > 80 ? oldText[..80] + "..." : oldText;
                var closest = FindClosestMatch(original, oldText);
                var hint = "";
                if (closest is not null)
                {
                    var c = closest.Value;
                    hint = $" - Closest match (L{c.line}, {c.similarity:P0} similar): \"{c.text}\"";
                    if (!string.IsNullOrEmpty(c.beforeContext))
                        hint += $"\n  <- before: \"{c.beforeContext}\"";
                    if (!string.IsNullOrEmpty(c.afterContext))
                        hint += $"\n  -> after:  \"{c.afterContext}\"";
                }
                errors.Add($"old_string not found in {relPath}: \"{snippet.Replace("\n", "\\n").Replace("\r", "\\r")}\"{hint}");
                continue;
            }

            if (op.ReplaceAll == true)
            {
                foreach (var candidate in candidates)
                {
                    replacementCount++;
                    matches.Add((candidate.Index, candidate.Length, newText));
                    usedRanges.Add((candidate.Index, candidate.Index + candidate.Length));
                }
                continue;
            }

            if (candidates.Count > 1)
            {
                var locations = string.Join(", ", candidates.Take(5).Select(c => $"L{GetLineNumberOf(original, c.Index)} ({c.Strategy})"));
                errors.Add($"old_string matches {candidates.Count} locations in {relPath}: {locations}. Use scope_start_line/scope_end_line or add more context to old_text.");
                continue;
            }

            var match = candidates[0];
            replacementCount++;
            matches.Add((match.Index, match.Length, newText));
            usedRanges.Add((match.Index, match.Index + match.Length));
            if (!match.Strategy.Equals("exact", StringComparison.Ordinal))
                errors.Add($"old_string matched in {relPath} at L{GetLineNumberOf(original, match.Index)} using {match.Strategy} matching.");
        }
        return matches;
    }

    private static IReadOnlyList<TextMatch> FindReplacementCandidates(string original, string oldText)
    {
        var exact = FindLiteralMatches(original, oldText, "exact");
        if (exact.Count > 0)
            return exact;

        if (IsAmbiguousBlockPattern(oldText, original))
            return [];

        var whitespace = FindNormalizedMatches(
            original,
            oldText,
            "whitespace-tolerant",
            static c => char.IsWhiteSpace(c) ? null : c.ToString());
        if (whitespace.Count > 0)
            return whitespace;

        return FindNormalizedMatches(
            original,
            oldText,
            "punctuation-normalized",
            NormalizePunctuationChar);
    }

    private static bool IsAmbiguousBlockPattern(string oldText, string original)
    {
        var normalized = Regex.Replace(oldText, @"\s+", " ");
        var catchCount = Regex.Matches(normalized, @"\bcatch\s*\(").Count;
        var closeCount = Regex.Matches(
            normalized, @"\}\s*(catch|finally|try|else|namespace|class|struct)\b").Count;

        if (catchCount >= 2 || closeCount >= 2)
            return true;

        var normOld = Regex.Replace(oldText, @"\s+", " ").Trim();
        var normOrg = Regex.Replace(original, @"\s+", " ");
        var cnt = 0;
        var idx = 0;
        while ((idx = normOrg.IndexOf(normOld, idx, StringComparison.Ordinal)) >= 0)
        {
            if (++cnt >= 2) return true;
            idx += normOld.Length;
        }

        return false;
    }

    private static List<TextMatch> FindLiteralMatches(string original, string oldText, string strategy)
    {
        var matches = new List<TextMatch>();
        var start = 0;
        while (start <= original.Length)
        {
            var idx = original.IndexOf(oldText, start, StringComparison.Ordinal);
            if (idx < 0)
                break;

            matches.Add(new TextMatch(idx, oldText.Length, strategy));
            start = idx + Math.Max(1, oldText.Length);
        }

        return matches;
    }

    private static List<TextMatch> FindNormalizedMatches(
        string original,
        string oldText,
        string strategy,
        Func<char, string?> normalize)
    {
        var source = BuildNormalizedIndex(original, normalize);
        var search = BuildNormalizedIndex(oldText, normalize);
        if (string.IsNullOrEmpty(search.Text) || string.IsNullOrEmpty(source.Text))
            return [];

        var matches = new List<TextMatch>();
        var start = 0;
        while (start <= source.Text.Length)
        {
            var idx = source.Text.IndexOf(search.Text, start, StringComparison.Ordinal);
            if (idx < 0)
                break;

            var end = idx + search.Text.Length - 1;
            if (idx < source.OriginalIndexes.Count && end < source.OriginalIndexes.Count)
            {
                var originalStart = source.OriginalIndexes[idx];
                var originalEnd = source.OriginalIndexes[end] + 1;
                matches.Add(new TextMatch(originalStart, originalEnd - originalStart, strategy));
            }

            start = idx + Math.Max(1, search.Text.Length);
        }

        return matches;
    }

    private static NormalizedTextIndex BuildNormalizedIndex(string value, Func<char, string?> normalize)
    {
        var text = new StringBuilder();
        var indexes = new List<int>();

        for (var i = 0; i < value.Length; i++)
        {
            var normalized = normalize(value[i]);
            if (string.IsNullOrEmpty(normalized))
                continue;

            foreach (var c in normalized)
            {
                text.Append(c);
                indexes.Add(i);
            }
        }

        return new NormalizedTextIndex(text.ToString(), indexes);
    }

    private static string? NormalizePunctuationChar(char c)
    {
        if (char.IsWhiteSpace(c))
            return null;

        return c switch
        {
            '\u2018' or '\u2019' or '\u201A' or '\u201B' => "'",
            '\u201C' or '\u201D' or '\u201E' or '\u201F' => "\"",
            '\u2010' or '\u2011' or '\u2012' or '\u2013' or '\u2014' or '\u2212' => "-",
            '\u2026' => "...",
            '\u00A0' => null,
            _ => c.ToString(),
        };
    }

    private static string ApplyReplacements(string original, List<(int Index, int Length, string NewText)> replacements)
    {
        if (replacements.Count == 0) return original;
        replacements.Sort((a, b) => b.Index.CompareTo(a.Index));
        var result = original;
        foreach (var (idx, len, newTxt) in replacements)
            result = result.Remove(idx, len).Insert(idx, newTxt);
        return result;
    }

    private static string ApplyRegexOperation(string input, FilePatchOperation op, ref int replacementCount)
    {
                if (string.IsNullOrEmpty(op.Pattern))
            throw new InvalidOperationException("regexReplace operation requires 'pattern'. Pre-validation should have caught this.");

        var options = RegexOptions.None;
        if (op.Options?.Contains("ignoreCase", StringComparison.OrdinalIgnoreCase) == true)
            options |= RegexOptions.IgnoreCase;
        if (op.Options?.Contains("multiline", StringComparison.OrdinalIgnoreCase) == true)
            options |= RegexOptions.Multiline;
        if (op.Options?.Contains("singleline", StringComparison.OrdinalIgnoreCase) == true)
            options |= RegexOptions.Singleline;

        var count = 0;
        var replacement = op.Replacement ?? string.Empty;
        var output = Regex.Replace(input, op.Pattern, match =>
        {
            count++;
            return match.Result(replacement);
        }, options);
        replacementCount += count;
        return output;
    }

    private static string ApplyLineOperations(string input, IReadOnlyList<FilePatchOperation> ops,
        ref int replacementCount, List<string> errors, string relPath)
    {
        var fileLines = input.Replace("\r\n", "\n").Split('\n').ToList();
        var lineOps = new List<(int StartLine, int EndLine, string NewText, string Action,
            string? AnchorBefore, string? AnchorAfter)>();

        foreach (var op in ops)
        {
            var type = (op.Type ?? "").Trim().ToLowerInvariant();
            if (type != "insert" && type != "delete" && type != "replace_lines")
                continue;

            var startLine = op.StartLine ?? 0;
            var endLine = op.EndLine ?? startLine;
            var newText = op.NewText ?? "";

            if (string.IsNullOrWhiteSpace(op.AnchorBefore) && string.IsNullOrWhiteSpace(op.AnchorAfter))
            {
                errors.Add($"line operation '{type}' in {relPath}: no anchor_before or anchor_after provided. " +
                    "Line numbers may be stale - provide anchor_before and anchor_after for safe auto-correction.");
            }

            lineOps.Add((startLine, endLine, newText, type,
                op.AnchorBefore, op.AnchorAfter));
        }

        if (lineOps.Count == 0) return input;

        static string NormLine(string s) =>
            new string(s.Where(c => !char.IsWhiteSpace(c) || c == ' ').ToArray()).Trim();

        static string? NormOrNull(string? s) =>
            string.IsNullOrWhiteSpace(s) ? null : NormLine(s);

        lineOps.Sort((a, b) => b.StartLine.CompareTo(a.StartLine));
        foreach (var (sl, el, newText, action, anchorBefore, anchorAfter) in lineOps)
        {
            var normBefore = NormOrNull(anchorBefore);
            var normAfter = NormOrNull(anchorAfter);

            int resolvedStart = sl;
            int resolvedEnd = el;
            bool useOriginalPosition = true;

            if (normBefore != null || normAfter != null)
            {
                var beforeMatch = true;
                var afterMatch = true;

                if (normBefore != null)
                {
                    if (sl < 1 || sl > fileLines.Count) beforeMatch = false;
                    else beforeMatch = NormLine(fileLines[sl - 1]) == normBefore;
                }
                if (normAfter != null)
                {
                    if (el < 0 || el >= fileLines.Count) afterMatch = false;
                    else afterMatch = NormLine(fileLines[el]) == normAfter;
                }

                if (!beforeMatch || !afterMatch)
                {
                    useOriginalPosition = false;
                    var candidates = new List<int>();

                    for (int i = 0; i < fileLines.Count; i++)
                    {
                        var bc = normBefore == null || (i > 0 && NormLine(fileLines[i - 1]) == normBefore);
                        var ac = normAfter == null || (i < fileLines.Count - 1 && NormLine(fileLines[i]) == normAfter);

                        if (bc && ac)
                            candidates.Add(i + 1);
                    }

                    if (candidates.Count == 1)
                    {
                        resolvedStart = candidates[0];
                        resolvedEnd = action == "insert" ? resolvedStart : resolvedStart + (el - sl);
                        errors.Add($"line operation '{action}' in {relPath}: line shifted from L{sl} to L{resolvedStart} - auto-corrected");
                        useOriginalPosition = true;
                    }
                    else if (candidates.Count > 1)
                    {
                        errors.Add($"line operation '{action}' in {relPath}: anchors matched at {candidates.Count} positions " +
                            $"(L{string.Join(", L", candidates.Take(5))}). Provide more specific anchors to disambiguate.");
                        continue;
                    }
                    else
                    {
                        var details = new List<string>();
                        if (normBefore != null)
                        {
                            var bestBefore = FindClosestLine(fileLines, anchorBefore!);
                            if (bestBefore != null)
                                details.Add($"  anchor_before closest: L{bestBefore.Value.line} \"{bestBefore.Value.text.Trim()}\" ({bestBefore.Value.similarity:P0} similar)");
                        }
                        if (normAfter != null)
                        {
                            var bestAfter = FindClosestLine(fileLines, anchorAfter!);
                            if (bestAfter != null)
                                details.Add($"  anchor_after closest: L{bestAfter.Value.line} \"{bestAfter.Value.text.Trim()}\" ({bestAfter.Value.similarity:P0} similar)");
                        }
                        var detailStr = details.Count > 0 ? "\n" + string.Join("\n", details) : "";
                        errors.Add($"line operation '{action}' in {relPath}: anchors not found in current file - file may have changed significantly, re-read and try again.{detailStr}");
                        continue;
                    }
                }
            }

            if (!useOriginalPosition) continue;

            if (resolvedStart < 0 || resolvedStart > fileLines.Count)
            {
                errors.Add($"line operation '{action}' failed in {relPath}: start_line {resolvedStart} out of range (file has {fileLines.Count} lines)");
                continue;
            }
            if (resolvedEnd < resolvedStart || resolvedEnd > fileLines.Count)
            {
                errors.Add($"line operation '{action}' failed in {relPath}: end_line {resolvedEnd} out of range");
                continue;
            }

            var insertedLines = string.IsNullOrEmpty(newText) ? Array.Empty<string>()
                : newText.Replace("\r\n", "\n").Split('\n');

            if (action == "insert")
            {
                var insertAt = resolvedStart >= fileLines.Count ? fileLines.Count : resolvedStart;
                fileLines.InsertRange(insertAt, insertedLines);
            }
            else
            {
                var count = resolvedEnd - resolvedStart + 1;
                fileLines.RemoveRange(resolvedStart - 1, count);
                if (action == "replace_lines")
                    fileLines.InsertRange(resolvedStart - 1, insertedLines);
            }
            replacementCount++;
        }

        return string.Join("\n", fileLines);
    }

    private static (int line, string text, double similarity)? FindClosestLine(List<string> lines, string target)
    {
        if (string.IsNullOrEmpty(target) || lines.Count == 0) return null;
        var normTarget = new string(target.Where(c => !char.IsWhiteSpace(c) || c == ' ').ToArray()).Trim();
        var bestSim = 0.0;
        var bestIdx = -1;

        for (int i = 0; i < lines.Count; i++)
        {
            var normLine = new string(lines[i].Where(c => !char.IsWhiteSpace(c) || c == ' ').ToArray()).Trim();
            var sim = LcsSimilarity(normLine, normTarget);
            if (sim > bestSim)
            {
                bestSim = sim;
                bestIdx = i;
            }
        }
        return bestIdx >= 0 ? (bestIdx + 1, lines[bestIdx], bestSim) : null;
    }

    private static double LcsSimilarity(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0;
        var lcsLen = LongestCommonSubsequenceLength(a, b);
        return (double)lcsLen / Math.Max(a.Length, b.Length);
    }

    private static int LongestCommonSubsequenceLength(string a, string b)
    {
        if (a.Length > b.Length) (a, b) = (b, a);
        var prev = new int[a.Length + 1];
        for (int i = 1; i <= b.Length; i++)
        {
            var curr = new int[a.Length + 1];
            for (int j = 1; j <= a.Length; j++)
            {
                if (b[i - 1] == a[j - 1])
                    curr[j] = prev[j - 1] + 1;
                else
                    curr[j] = Math.Max(prev[j], curr[j - 1]);
            }
            prev = curr;
        }
        return prev[a.Length];
    }

    private static string GenerateSimpleDiff(string original, string current)
    {
        var sb = new StringBuilder();
        var oldLines = original.Split('\n');
        var newLines = current.Split('\n');
        int maxShow = 10, shown = 0;
        for (int i = 0; i < Math.Max(oldLines.Length, newLines.Length) && shown < maxShow; i++)
        {
            var o = i < oldLines.Length ? oldLines[i].TrimEnd('\r') : null;
            var n = i < newLines.Length ? newLines[i].TrimEnd('\r') : null;
            if (o == n) continue;
            shown++;
            if (o != null) sb.AppendLine($"- {o}");
            if (n != null) sb.AppendLine($"+ {n}");
        }
        if (shown >= maxShow) sb.AppendLine("... (more changes)");
        return sb.Length > 0 ? sb.ToString() : "(no visible line changes)";
    }

    private static int GetLineNumberOf(string content, int charIndex)
    {
        int pos = 0;
        var ln = content.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < ln.Length; i++)
        {
            pos += ln[i].Length + 1;
            if (pos > charIndex) return i + 1;
        }
        return ln.Length;
    }

    private static (int line, string text, double similarity, string beforeContext, string afterContext)? FindClosestMatch(string fileContent, string searchText)
    {
        if (string.IsNullOrEmpty(searchText) || searchText.Length < 3) return null;
        var clines = fileContent.Replace("\r\n", "\n").Split('\n');
        var searchLines = searchText.Split('\n');
        var bestSim = 0.0;
        var bestStartLine = 0;
        var bestText = "";
        var bestBefore = "";
        var bestAfter = "";

        int maxWindow = Math.Min(searchLines.Length + 10, Math.Min(clines.Length, 20));
        for (int windowSize = Math.Max(searchLines.Length, 1); windowSize <= maxWindow; windowSize++)
        {
            for (int startLine = 0; startLine <= clines.Length - windowSize; startLine++)
            {
                var endLine = startLine + windowSize - 1;
                var window = string.Join("\n", clines[startLine..(endLine + 1)]);
                if (window.Length < searchText.Length / 3) continue;
                var sim = LongestCommonSubstringRatio(window, searchText);
                if (sim > bestSim)
                {
                    bestSim = sim;
                    bestStartLine = startLine + 1;
                    bestText = window.Length > 80 ? window[..80].TrimEnd() + "..." : window.TrimEnd();
                    bestBefore = startLine > 0 ? string.Join("\n", clines[Math.Max(0, startLine - 3)..startLine]) : "";
                    bestAfter = endLine < clines.Length - 1 ? string.Join("\n", clines[(endLine + 1)..Math.Min(clines.Length, endLine + 4)]) : "";
                }
            }
        }
        return bestSim > 0 ? (bestStartLine, bestText, bestSim, bestBefore, bestAfter) : null;
    }

    private static double LongestCommonSubstringRatio(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0;
        var maxLen = 0;
        var prev = new int[b.Length + 1];
        for (int i = 0; i < a.Length; i++)
        {
            var curr = new int[b.Length + 1];
            for (int j = 0; j < b.Length; j++)
            {
                if (char.ToLowerInvariant(a[i]) == char.ToLowerInvariant(b[j]))
                {
                    curr[j + 1] = prev[j] + 1;
                    if (curr[j + 1] > maxLen) maxLen = curr[j + 1];
                }
            }
            prev = curr;
        }
        return (double)maxLen / Math.Max(a.Length, b.Length);
    }
}

public sealed record FilePatchArgs
{
    [ToolParam("Single file path to patch.")]
    public string? Path { get; init; }

    [ToolParam("Unified diff text to apply transactionally. When set, path/operations are ignored and dry_run still defaults to true.")]
    [JsonPropertyName("patch_text")]
    public string? PatchText { get; init; }

    [ToolParam("Operations for the single file patch.")]
    public IReadOnlyList<FilePatchOperation>? Operations { get; init; }

    [ToolParam("Batch patches. Each item has path and operations.")]
    public IReadOnlyList<FilePatchItem>? Patches { get; init; }

    [ToolParam("Reason for patching. Required when patching agent private files.")]
    public string? Reason { get; init; }

    [ToolParam("If true, return diff preview without modifying files. Default: true (mandatory preview - set false to actually apply changes).")]
    public bool? DryRun { get; init; }

    [ToolParam("Optional 1-based start line to scope text replacements within this region.")]
    public int? ScopeStartLine { get; init; }

    [ToolParam("Optional 1-based end line to scope text replacements within this region.")]
    public int? ScopeEndLine { get; init; }
}

internal sealed record TextMatch(int Index, int Length, string Strategy);

internal sealed record NormalizedTextIndex(string Text, IReadOnlyList<int> OriginalIndexes);

internal sealed record UnifiedDiffParseResult(bool Success, IReadOnlyList<UnifiedDiffFile> Files, string? Error)
{
    public static UnifiedDiffParseResult Ok(IReadOnlyList<UnifiedDiffFile> files) => new(true, files, null);
    public static UnifiedDiffParseResult Fail(string error) => new(false, [], error);
}

internal sealed record UnifiedDiffApplyResult(bool Success, string? Content, string? Error)
{
    public static UnifiedDiffApplyResult Ok(string content) => new(true, content, null);
    public static UnifiedDiffApplyResult Fail(string error) => new(false, null, error);
}

internal sealed record UnifiedDiffFile(string Path, IReadOnlyList<UnifiedDiffHunk> Hunks);

internal sealed record UnifiedDiffHunk(int OldStart, int OldCount, int NewStart, int NewCount, IReadOnlyList<UnifiedDiffLine> Lines);

internal sealed record UnifiedDiffLine(char Kind, string Text);

internal static class UnifiedDiffPatchRunner
{
    public static ToolExecutionResult Apply(
        string patchText,
        string? reason,
        bool? dryRun,
        ToolExecutionContext context,
        PuddingDataPaths dataPaths,
        AuditLogger audit,
        string toolId)
    {
        var parsed = UnifiedDiffParser.Parse(patchText);
        if (!parsed.Success)
            return ToolExecutionResult.Fail(parsed.Error ?? "Invalid unified diff.");

        var touchedFiles = new List<(UnifiedDiffFile Patch, string FullPath, string Original, string Current, OperationZone Zone)>();
        foreach (var patch in parsed.Files)
        {
            if (!HostFileToolPaths.TryResolveInsideWorkspace(patch.Path, out var fullPath, out var resolveError, skipWorkspaceCheck: context.IsYoloMode))
            {
                audit.Write(OperationZone.External, toolId, context.AgentInstanceId,
                    patch.Path, reason, false, 0, context.Trace);
                return ToolExecutionResult.Fail(resolveError);
            }

            if (!File.Exists(fullPath))
            {
                var zone = OperationZoneClassifier.ClassifyPath(
                    fullPath, dataPaths, context.WorkspaceId, context.AgentInstanceId);
                audit.Write(zone, toolId, context.AgentInstanceId,
                    patch.Path, reason, false, 0, context.Trace);
                return ToolExecutionResult.Fail($"File not found: {patch.Path}");
            }

            var fileZone = OperationZoneClassifier.ClassifyPath(
                fullPath, dataPaths, context.WorkspaceId, context.AgentInstanceId);
            if (fileZone == OperationZone.AgentPrivate && string.IsNullOrWhiteSpace(reason))
            {
                audit.Write(fileZone, toolId, context.AgentInstanceId,
                    patch.Path, reason, false, 0, context.Trace);
                return ToolExecutionResult.Fail(
                    "Patching agent private files requires a 'reason' parameter. Please explain the purpose of this patch.");
            }

            var original = File.ReadAllText(fullPath, Encoding.UTF8);
            var applyResult = UnifiedDiffApplier.Apply(original, patch);
            if (!applyResult.Success)
            {
                audit.Write(fileZone, toolId, context.AgentInstanceId,
                    patch.Path, reason, false, 0, context.Trace);
                return ToolExecutionResult.Fail(applyResult.Error ?? $"Failed to apply patch to {patch.Path}");
            }

            touchedFiles.Add((patch, fullPath, original, applyResult.Content!, fileZone));
        }

        var isDryRun = dryRun != false;
        var summaries = touchedFiles
            .Select(file => $"{Path.GetRelativePath(HostFileToolPaths.WorkspaceRoot, file.FullPath)}: {(isDryRun ? "preview" : "patched")}\n{GenerateSimpleDiff(file.Original, file.Current)}")
            .ToArray();

        if (isDryRun)
            return ToolExecutionResult.Ok(string.Join(Environment.NewLine, summaries));

        var sw = Stopwatch.StartNew();
        var backups = new List<(string FullPath, string BackupPath, OperationZone Zone, string RequestedPath)>();
        var tempFiles = new List<string>();
        try
        {
            foreach (var file in touchedFiles)
            {
                var backup = file.FullPath + ".bak." + Guid.NewGuid().ToString("N")[..8];
                var temp = file.FullPath + ".tmp." + Guid.NewGuid().ToString("N")[..8];
                File.Copy(file.FullPath, backup, overwrite: false);
                File.WriteAllText(temp, file.Current, Encoding.UTF8);
                backups.Add((file.FullPath, backup, file.Zone, file.Patch.Path));
                tempFiles.Add(temp);
            }

            for (var i = 0; i < touchedFiles.Count; i++)
                File.Move(tempFiles[i], touchedFiles[i].FullPath, overwrite: true);

            foreach (var file in touchedFiles)
            {
                audit.Write(file.Zone, toolId, context.AgentInstanceId,
                    file.Patch.Path, reason, true, sw.ElapsedMilliseconds, context.Trace);
            }

            return ToolExecutionResult.Ok(string.Join(Environment.NewLine, summaries));
        }
        catch (Exception ex)
        {
            foreach (var backup in backups)
            {
                try
                {
                    if (File.Exists(backup.BackupPath))
                        File.Move(backup.BackupPath, backup.FullPath, overwrite: true);
                    audit.Write(backup.Zone, toolId, context.AgentInstanceId,
                        backup.RequestedPath, reason, false, sw.ElapsedMilliseconds, context.Trace);
                }
                catch
                {
                }
            }

            return ToolExecutionResult.Fail($"Failed to write unified patch transaction: {ex.Message}");
        }
        finally
        {
            foreach (var temp in tempFiles)
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
            foreach (var backup in backups)
            {
                try { if (File.Exists(backup.BackupPath)) File.Delete(backup.BackupPath); } catch { }
            }
        }
    }

    private static string GenerateSimpleDiff(string original, string current)
    {
        var sb = new StringBuilder();
        var oldLines = original.Split('\n');
        var newLines = current.Split('\n');
        int maxShow = 10, shown = 0;
        for (int i = 0; i < Math.Max(oldLines.Length, newLines.Length) && shown < maxShow; i++)
        {
            var o = i < oldLines.Length ? oldLines[i].TrimEnd('\r') : null;
            var n = i < newLines.Length ? newLines[i].TrimEnd('\r') : null;
            if (o == n) continue;
            shown++;
            if (o != null) sb.AppendLine($"- {o}");
            if (n != null) sb.AppendLine($"+ {n}");
        }
        if (shown >= maxShow) sb.AppendLine("... (more changes)");
        return sb.Length > 0 ? sb.ToString() : "(no visible line changes)";
    }
}

internal static class UnifiedDiffParser
{
    private static readonly Regex s_hunkHeader = new(
        @"^@@ -(?<oldStart>\d+)(?:,(?<oldCount>\d+))? \+(?<newStart>\d+)(?:,(?<newCount>\d+))? @@",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static UnifiedDiffParseResult Parse(string patchText)
    {
        var lines = patchText.Replace("\r\n", "\n").Split('\n');
        var files = new List<UnifiedDiffFile>();
        string? currentPath = null;
        var hunks = new List<UnifiedDiffHunk>();
        var i = 0;

        while (i < lines.Length)
        {
            var line = lines[i];
            if (line.StartsWith("--- ", StringComparison.Ordinal))
            {
                if (currentPath is not null)
                {
                    files.Add(new UnifiedDiffFile(currentPath, hunks));
                    hunks = [];
                }

                if (i + 1 >= lines.Length || !lines[i + 1].StartsWith("+++ ", StringComparison.Ordinal))
                    return UnifiedDiffParseResult.Fail("Unified diff file header must include matching +++ line.");

                currentPath = CleanDiffPath(lines[i + 1][4..].Trim());
                if (string.IsNullOrWhiteSpace(currentPath) || currentPath == "/dev/null")
                    return UnifiedDiffParseResult.Fail("Unified diff creates new files or uses an empty target path; file_patch patch_text currently patches existing files only.");

                i += 2;
                continue;
            }

            if (line.StartsWith("@@ ", StringComparison.Ordinal))
            {
                if (currentPath is null)
                    return UnifiedDiffParseResult.Fail("Unified diff hunk appeared before a file header.");

                var match = s_hunkHeader.Match(line);
                if (!match.Success)
                    return UnifiedDiffParseResult.Fail($"Invalid unified diff hunk header: {line}");

                var hunkLines = new List<UnifiedDiffLine>();
                i++;
                while (i < lines.Length &&
                       !lines[i].StartsWith("@@ ", StringComparison.Ordinal) &&
                       !lines[i].StartsWith("--- ", StringComparison.Ordinal))
                {
                    var hunkLine = lines[i];
                    if (hunkLine.Length == 0)
                    {
                        hunkLines.Add(new UnifiedDiffLine(' ', string.Empty));
                        i++;
                        continue;
                    }

                    var kind = hunkLine[0];
                    if (kind is not (' ' or '+' or '-'))
                    {
                        if (hunkLine.StartsWith("\\ No newline at end of file", StringComparison.Ordinal))
                        {
                            i++;
                            continue;
                        }

                        return UnifiedDiffParseResult.Fail($"Invalid unified diff hunk line: {hunkLine}");
                    }

                    hunkLines.Add(new UnifiedDiffLine(kind, hunkLine[1..]));
                    i++;
                }

                hunks.Add(new UnifiedDiffHunk(
                    int.Parse(match.Groups["oldStart"].Value),
                    ParseOptionalCount(match.Groups["oldCount"].Value),
                    int.Parse(match.Groups["newStart"].Value),
                    ParseOptionalCount(match.Groups["newCount"].Value),
                    hunkLines));
                continue;
            }

            i++;
        }

        if (currentPath is not null)
            files.Add(new UnifiedDiffFile(currentPath, hunks));

        if (files.Count == 0)
            return UnifiedDiffParseResult.Fail("No files found in unified diff.");
        if (files.Any(f => f.Hunks.Count == 0))
            return UnifiedDiffParseResult.Fail("Unified diff file has no hunks.");

        return UnifiedDiffParseResult.Ok(files);
    }

    private static int ParseOptionalCount(string value) =>
        string.IsNullOrWhiteSpace(value) ? 1 : int.Parse(value);

    private static string CleanDiffPath(string path)
    {
        var tabIndex = path.IndexOf('\t');
        if (tabIndex >= 0)
            path = path[..tabIndex];
        if (path.StartsWith("a/", StringComparison.Ordinal) || path.StartsWith("b/", StringComparison.Ordinal))
            path = path[2..];
        return path.Trim();
    }
}

internal static class UnifiedDiffApplier
{
    public static UnifiedDiffApplyResult Apply(string original, UnifiedDiffFile patch)
    {
        var current = original.Replace("\r\n", "\n");
        foreach (var hunk in patch.Hunks)
        {
            var expected = hunk.Lines
                .Where(line => line.Kind is ' ' or '-')
                .Select(line => line.Text)
                .ToArray();
            var replacement = hunk.Lines
                .Where(line => line.Kind is ' ' or '+')
                .Select(line => line.Text)
                .ToArray();

            var currentLines = current.Split('\n').ToList();
            var candidates = FindHunkCandidates(currentLines, expected, hunk.OldStart);
            if (candidates.Count == 0)
                return UnifiedDiffApplyResult.Fail($"Hunk for {patch.Path} starting at old line {hunk.OldStart} did not match. Re-read the file and regenerate the patch.");
            if (candidates.Count > 1)
                return UnifiedDiffApplyResult.Fail($"Hunk for {patch.Path} starting at old line {hunk.OldStart} matched {candidates.Count} locations. Add more context lines.");

            var index = candidates[0];
            currentLines.RemoveRange(index, expected.Length);
            currentLines.InsertRange(index, replacement);
            current = string.Join("\n", currentLines);
        }

        if (original.Contains("\r\n", StringComparison.Ordinal))
            current = current.Replace("\n", "\r\n");

        return UnifiedDiffApplyResult.Ok(current);
    }

    private static List<int> FindHunkCandidates(IReadOnlyList<string> lines, IReadOnlyList<string> expected, int oldStart)
    {
        var candidates = new List<int>();
        var preferred = Math.Max(0, oldStart - 1);
        if (MatchesAt(lines, expected, preferred))
            candidates.Add(preferred);

        for (var i = 0; i <= lines.Count - expected.Count; i++)
        {
            if (i == preferred)
                continue;
            if (MatchesAt(lines, expected, i))
                candidates.Add(i);
        }

        return candidates;
    }

    private static bool MatchesAt(IReadOnlyList<string> lines, IReadOnlyList<string> expected, int index)
    {
        if (index < 0 || index + expected.Count > lines.Count)
            return false;

        for (var i = 0; i < expected.Count; i++)
        {
            if (!string.Equals(lines[index + i], expected[i], StringComparison.Ordinal))
                return false;
        }

        return true;
    }
}

public sealed record FilePatchItem
{
    [ToolParam("File path to patch.")]
    public required string Path { get; init; }

    [ToolParam("Patch operations.")]
    public required IReadOnlyList<FilePatchOperation> Operations { get; init; }
}

public sealed record FilePatchOperation
{
    [ToolParam("Operation type: replace, insert, delete, replace_lines, or regexReplace.")]
    public string? Type { get; init; }

    [ToolParam("Text to replace for replace operations.")]
    [JsonPropertyName("old_text")]
    public string? OldText { get; init; }

    [ToolParam("Replacement text for replace/replace_lines/insert operations.")]
    [JsonPropertyName("new_text")]
    public string? NewText { get; init; }

    [ToolParam("Replace every occurrence when true; otherwise only the first occurrence.")]
    [JsonPropertyName("replace_all")]
    public bool? ReplaceAll { get; init; }

    [ToolParam("1-based start line for insert/delete/replace_lines operations.")]
    public int? StartLine { get; init; }

    [ToolParam("1-based end line (inclusive) for delete/replace_lines operations.")]
    public int? EndLine { get; init; }

    [ToolParam("Regex pattern for regexReplace operations.")]
    public string? Pattern { get; init; }

    [ToolParam("Regex replacement text.")]
    public string? Replacement { get; init; }

    [ToolParam("Regex options: ignoreCase|multiline|singleline.")]
    public string? Options { get; init; }

    [ToolParam("Full content of the line immediately BEFORE the target range (whitespace-insensitive match). Used to verify correct line number and auto-correct if file shifted.")]
    [JsonPropertyName("anchor_before")]
    public string? AnchorBefore { get; init; }

    [ToolParam("Full content of the line immediately AFTER the target range (whitespace-insensitive match).")]
    [JsonPropertyName("anchor_after")]
    public string? AnchorAfter { get; init; }
}
