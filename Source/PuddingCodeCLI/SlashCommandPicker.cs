using Spectre.Console;
using System.Text;
using System.Text.RegularExpressions;

namespace PuddingCodeCLI;

/// <summary>
/// Provides an interactive slash-command picker.
/// When the user types '/', a filterable menu appears below the prompt.
/// Arrow keys navigate, Enter selects, Escape/Backspace cancels.
/// </summary>
public static class SlashCommandPicker
{
    public record CommandEntry(string Command, string Description);
    private const int MaxMenuRows = 8;
    private const int MaxHistory = 200;
    private static readonly List<string> s_history = [];

    private static readonly CommandEntry[] s_commands =
    [
        new("/help",            "Show all commands"),
        new("/open",            "Open a project directory"),
        new("/model",           "List / manage LLM providers"),
        new("/model add",       "Add a new LLM provider"),
        new("/model use",       "Switch active provider"),
        new("/model remove",    "Remove a provider"),
        new("/provider",        "Alias of /model"),
        new("/provider add",    "Add a new LLM provider"),
        new("/provider use",    "Switch active provider"),
        new("/provider remove", "Remove a provider"),
        new("/undo",            "Undo last N tool snapshots"),
        new("/snapshot",        "Create a manual snapshot"),
        new("/history",         "List recent snapshots"),
        new("/config",          "Show current config"),
        new("/config check",    "Validate configuration"),
        new("/config fix",      "Apply safe config auto-fixes"),
        new("/swarm",           "Start a swarm session"),
        new("/swarm status",    "Show swarm worker status"),
        new("/swarm continue",  "Resume latest pending swarm tasks"),
        new("/swarm cancel",    "Cancel active swarm"),
        new("/debug",           "Toggle subconscious stream"),
        new("/memory",          "Show memory status"),
        new("/memory status",   "Show memory status"),
        new("/memory rebuild",  "Rebuild memory index"),
        new("/memory compact",  "Run memory maintenance"),
        new("/prompt",          "Prompt template commands"),
        new("/prompt status",   "Show prompt template status"),
        new("/prompt init",     "Create prompt template files"),
        new("/hook",            "Hook commands"),
        new("/hook status",     "Show hook status"),
        new("/hook enable",     "Enable hook (metrics/audit_file/external)"),
        new("/hook disable",    "Disable hook (metrics/audit_file/external)"),
        new("/status",          "Show runtime status"),
        new("/todo",            "Show todo list"),
        new("/todo list",       "List todo items"),
        new("/todo add",        "Add a todo item"),
        new("/todo done",       "Mark todo item done"),
        new("/todo remove",     "Remove todo item"),
        new("/ui scroll up",    "Scroll interaction stream up"),
        new("/ui scroll down",  "Scroll interaction stream down"),
        new("/ui scroll top",   "Scroll interaction stream to top"),
        new("/ui scroll bottom","Scroll interaction stream to bottom"),
        new("/ui tab next",     "Switch left pane tab next"),
        new("/ui tab prev",     "Switch left pane tab prev"),
        new("/ui tab main",     "Switch to main tab"),
        new("/ui tab swarm",    "Switch to swarm tab"),
        new("/ui tab todo",     "Switch to todo tab"),
        new("/ui view next",    "Switch center pane view"),
        new("/ui view prev",    "Switch center pane view"),
        new("/ui view stream",  "Center pane: interaction stream"),
        new("/ui view swarm",   "Center pane: swarm workers"),
        new("/ui view todo",    "Center pane: todo board"),
        new("/ui view review",  "Center pane: diff review"),
        new("/ui worker next",  "Focus next swarm worker"),
        new("/ui worker prev",  "Focus previous swarm worker"),
        new("/ui worker clear", "Clear focused swarm worker"),
        new("/review",          "Review commands"),
        new("/review status",   "Show review status"),
        new("/review diff",     "Generate diff preview"),
        new("/review list",     "List review queue"),
        new("/review use",      "Select review queue item"),
        new("/review approve",  "Approve current diff"),
        new("/review approve apply", "Approve and run apply hooks"),
        new("/review reject",   "Reject current diff"),
        new("/review reject discard", "Reject and discard tracked changes"),
        new("/review reject discard --yes", "Reject+discard without confirmation"),
        new("/review reset",    "Reset review state"),
        new("/clear",           "Clear screen and redraw UI"),
        new("/exit",            "Exit Pudding"),
    ];

    /// <summary>
    /// Reads a line from the terminal with slash-command autocomplete.
    /// Returns the final input string (may be a command or a normal message).
    /// Returns null if the user pressed Ctrl+C.
    /// </summary>
    public static string? ReadLine(string prompt)
    {
        var promptText = NormalizePrompt(prompt);
        var buffer = new StringBuilder();
        var pickerActive = false;
        var pickerIndex = 0;
        var historyCursor = -1;
        CommandEntry[] filtered = [];
        var menuRowsRendered = 0;
        var menuTopRow = -1;
        var hintVisible = false;

        while (true)
        {
            RenderInputAndMenu(promptText, buffer.ToString(), pickerActive, filtered, pickerIndex, ref menuRowsRendered, ref menuTopRow, ref hintVisible);

            if (WindowsConsoleMouse.TryReadWheelDirection(out var wheelDir))
            {
                // Avoid dropping partially typed input; only use wheel when input is empty.
                if (buffer.Length == 0)
                {
                    ClearMenu(menuRowsRendered, menuTopRow);
                    ClearHint(hintVisible);
                    MoveCursorToInputRow();
                    Console.WriteLine();
                    return wheelDir < 0 ? "/ui scroll up" : "/ui scroll down";
                }

                continue;
            }

            ConsoleKeyInfo key;
            try { key = Console.ReadKey(intercept: true); }
            catch (InvalidOperationException) { return null; } // stdin redirected

            if (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control))
            {
                ClearMenu(menuRowsRendered, menuTopRow);
                MoveCursorToInputRow();
                Console.WriteLine();
                return null;
            }

            if (key.Key == ConsoleKey.PageUp)
            {
                ClearMenu(menuRowsRendered, menuTopRow);
                ClearHint(hintVisible);
                MoveCursorToInputRow();
                Console.WriteLine();
                return "/ui scroll up";
            }

            if (key.Key == ConsoleKey.PageDown)
            {
                ClearMenu(menuRowsRendered, menuTopRow);
                ClearHint(hintVisible);
                MoveCursorToInputRow();
                Console.WriteLine();
                return "/ui scroll down";
            }

            if (key.Key == ConsoleKey.Oem4 && key.Modifiers.HasFlag(ConsoleModifiers.Alt))
            {
                ClearMenu(menuRowsRendered, menuTopRow);
                ClearHint(hintVisible);
                MoveCursorToInputRow();
                Console.WriteLine();
                return "/ui tab prev";
            }

            if (key.Key == ConsoleKey.Oem6 && key.Modifiers.HasFlag(ConsoleModifiers.Alt))
            {
                ClearMenu(menuRowsRendered, menuTopRow);
                ClearHint(hintVisible);
                MoveCursorToInputRow();
                Console.WriteLine();
                return "/ui tab next";
            }

            if (key.Modifiers.HasFlag(ConsoleModifiers.Alt))
            {
                if (key.Key is ConsoleKey.D1 or ConsoleKey.NumPad1)
                {
                    ClearMenu(menuRowsRendered, menuTopRow);
                    ClearHint(hintVisible);
                    MoveCursorToInputRow();
                    Console.WriteLine();
                    return "/ui tab main";
                }
                if (key.Key is ConsoleKey.D2 or ConsoleKey.NumPad2)
                {
                    ClearMenu(menuRowsRendered, menuTopRow);
                    ClearHint(hintVisible);
                    MoveCursorToInputRow();
                    Console.WriteLine();
                    return "/ui tab swarm";
                }
                if (key.Key is ConsoleKey.D3 or ConsoleKey.NumPad3)
                {
                    ClearMenu(menuRowsRendered, menuTopRow);
                    ClearHint(hintVisible);
                    MoveCursorToInputRow();
                    Console.WriteLine();
                    return "/ui tab todo";
                }
                if (key.Key is ConsoleKey.D4 or ConsoleKey.NumPad4)
                {
                    ClearMenu(menuRowsRendered, menuTopRow);
                    ClearHint(hintVisible);
                    MoveCursorToInputRow();
                    Console.WriteLine();
                    return "/ui view next";
                }
                if (key.Key is ConsoleKey.D5 or ConsoleKey.NumPad5)
                {
                    ClearMenu(menuRowsRendered, menuTopRow);
                    ClearHint(hintVisible);
                    MoveCursorToInputRow();
                    Console.WriteLine();
                    return "/ui worker next";
                }
                if (key.Key is ConsoleKey.D6 or ConsoleKey.NumPad6)
                {
                    ClearMenu(menuRowsRendered, menuTopRow);
                    ClearHint(hintVisible);
                    MoveCursorToInputRow();
                    Console.WriteLine();
                    return "/ui worker prev";
                }
                if (key.Key is ConsoleKey.D7 or ConsoleKey.NumPad7)
                {
                    ClearMenu(menuRowsRendered, menuTopRow);
                    ClearHint(hintVisible);
                    MoveCursorToInputRow();
                    Console.WriteLine();
                    return "/ui worker clear";
                }
                if (key.Key is ConsoleKey.D8 or ConsoleKey.NumPad8)
                {
                    ClearMenu(menuRowsRendered, menuTopRow);
                    ClearHint(hintVisible);
                    MoveCursorToInputRow();
                    Console.WriteLine();
                    return "/ui view review";
                }
            }

            if (key.Key == ConsoleKey.L && key.Modifiers.HasFlag(ConsoleModifiers.Control))
            {
                ClearMenu(menuRowsRendered, menuTopRow);
                ClearHint(hintVisible);
                MoveCursorToInputRow();
                Console.WriteLine();
                return "/clear";
            }

            if (pickerActive && key.Key == ConsoleKey.UpArrow)
            {
                pickerIndex = Math.Max(0, pickerIndex - 1);
                continue;
            }

            if (pickerActive && key.Key == ConsoleKey.DownArrow)
            {
                pickerIndex = Math.Min(Math.Max(0, filtered.Length - 1), pickerIndex + 1);
                continue;
            }

            if (key.Key == ConsoleKey.Escape && pickerActive)
            {
                pickerActive = false;
                filtered = [];
                pickerIndex = 0;
                continue;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (buffer.Length > 0)
                    buffer.Remove(buffer.Length - 1, 1);
            }
            else if (key.Key == ConsoleKey.K && key.Modifiers.HasFlag(ConsoleModifiers.Control))
            {
                buffer.Clear();
                historyCursor = -1;
            }
            else if (key.Key == ConsoleKey.P && key.Modifiers.HasFlag(ConsoleModifiers.Control))
            {
                ApplyHistory(delta: -1, buffer, ref historyCursor);
            }
            else if (key.Key == ConsoleKey.N && key.Modifiers.HasFlag(ConsoleModifiers.Control))
            {
                ApplyHistory(delta: +1, buffer, ref historyCursor);
            }
            else if (key.Key == ConsoleKey.Tab && pickerActive)
            {
                if (filtered.Length > 0)
                {
                    var picked = filtered[Math.Clamp(pickerIndex, 0, filtered.Length - 1)].Command;
                    buffer.Clear();
                    buffer.Append(picked);
                }
            }
            else if (key.Key == ConsoleKey.Enter || (key.Key == ConsoleKey.J && key.Modifiers.HasFlag(ConsoleModifiers.Control)))
            {
                if (pickerActive && filtered.Length > 0)
                {
                    buffer.Clear();
                    buffer.Append(filtered[pickerIndex].Command);
                }

                pickerActive = false;
                ClearMenu(menuRowsRendered, menuTopRow);
                ClearHint(hintVisible);
                MoveCursorToInputRow();
                WriteInputLine(promptText, buffer.ToString());
                Console.WriteLine();
                var submitted = buffer.ToString();
                AddHistory(submitted);
                return submitted;
            }
            else if (TryGetInputChar(key, out var inputChar))
            {
                buffer.Append(inputChar);
                historyCursor = -1;
            }

            var input = buffer.ToString();
            pickerActive = input.Length > 0 && input[0] == '/';
            if (pickerActive)
            {
                filtered = Filter(input);
                if (filtered.Length == 0)
                {
                    pickerIndex = 0;
                }
                else
                {
                    pickerIndex = Math.Clamp(pickerIndex, 0, filtered.Length - 1);
                }
            }
            else
            {
                filtered = [];
                pickerIndex = 0;
            }
        }
    }

    // Helpers

    private static CommandEntry[] Filter(string prefix) =>
        s_commands
            .Where(c => c.Command.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToArray();

    private static bool IsSlashTriggerKey(ConsoleKeyInfo key) =>
        key.KeyChar == '/' || key.Key is ConsoleKey.Oem2 or ConsoleKey.Divide;

    private static bool TryGetInputChar(ConsoleKeyInfo key, out char value)
    {
        if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
        {
            value = key.KeyChar;
            return true;
        }

        if (IsSlashTriggerKey(key))
        {
            value = '/';
            return true;
        }

        value = '\0';
        return false;
    }

    private static void RenderInputAndMenu(
        string promptText,
        string inputText,
        bool pickerActive,
        CommandEntry[] filtered,
        int selectedIndex,
        ref int menuRowsRendered,
        ref int menuTopRow,
        ref bool hintVisible)
    {
        WriteFooterSeparator();
        WriteInputLine(promptText, inputText);

        if (!pickerActive)
        {
            ClearMenu(menuRowsRendered, menuTopRow);
            menuRowsRendered = 0;
            menuTopRow = -1;
            WriteHint();
            hintVisible = true;
            return;
        }

        if (hintVisible)
        {
            ClearHint(hintVisible);
            hintVisible = false;
        }
        DrawMenu(filtered, selectedIndex, ref menuRowsRendered, ref menuTopRow, promptText.Length + 1, inputText.Length);
    }

    private static void DrawMenu(
        CommandEntry[] entries,
        int selectedIndex,
        ref int menuRowsRendered,
        ref int menuTopRow,
        int promptLen,
        int inputLen)
    {
        var boundaryRow = FooterRow();
        var usableRowsAbove = Math.Max(0, boundaryRow);
        var maxRows = Math.Min(MaxMenuRows, usableRowsAbove);
        var visibleRows = Math.Max(1, Math.Min(entries.Length == 0 ? 1 : entries.Length, maxRows));
        var topRow = boundaryRow - visibleRows;

        ClearMenu(menuRowsRendered, menuTopRow);
        menuRowsRendered = visibleRows;
        menuTopRow = topRow;

        for (var i = 0; i < visibleRows; i++)
        {
            var row = topRow + i;
            if (row < 0 || row >= Console.BufferHeight) continue;
            Console.SetCursorPosition(0, row);
            Console.Write(new string(' ', SafeLineWidth()));
        }

        if (entries.Length == 0 || maxRows == 0)
        {
            if (maxRows > 0)
            {
                Console.SetCursorPosition(2, topRow);
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write("(no matching commands)");
                Console.ResetColor();
            }
            MoveCursorToInputText(promptLen, inputLen);
            return;
        }

        var start = 0;
        if (entries.Length > visibleRows)
            start = Math.Clamp(selectedIndex - visibleRows + 1, 0, entries.Length - visibleRows);

        var visibleEntries = entries.Skip(start).Take(visibleRows).ToArray();
        for (var i = 0; i < visibleEntries.Length; i++)
        {
            var row = topRow + i;
            Console.SetCursorPosition(0, row);
            Console.SetCursorPosition(2, row);

            var entry = visibleEntries[i];
            var isSelected = start + i == selectedIndex;
            var maxTextWidth = Math.Max(0, SafeLineWidth() - 2);
            var cmdText = entry.Command.PadRight(22);
            var descText = entry.Description ?? "";
            var lineText = $"{cmdText} {descText}";
            if (lineText.Length > maxTextWidth)
                lineText = lineText[..maxTextWidth];

            if (isSelected)
            {
                Console.BackgroundColor = ConsoleColor.DarkBlue;
                Console.ForegroundColor = ConsoleColor.White;
                Console.Write($" {lineText}");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write(cmdText.Length > maxTextWidth ? cmdText[..maxTextWidth] : cmdText);
                Console.ForegroundColor = ConsoleColor.Gray;
                if (maxTextWidth > cmdText.Length + 1)
                {
                    var descMax = maxTextWidth - cmdText.Length - 1;
                    var outDesc = descText.Length > descMax ? descText[..descMax] : descText;
                    Console.Write($" {outDesc}");
                }
                Console.ResetColor();
            }
        }

        MoveCursorToInputText(promptLen, inputLen);
    }

    private static void WriteInputLine(string promptText, string inputText)
    {
        var row = InputRow();
        Console.SetCursorPosition(0, row);
        Console.Write(new string(' ', SafeLineWidth()));
        Console.SetCursorPosition(0, row);

        var full = $"{promptText} {inputText}";
        var max = SafeLineWidth();
        if (full.Length > max)
            full = full[..max];
        Console.Write(full);
        MoveCursorToInputText(promptText.Length + 1, inputText.Length);
    }

    private static void MoveCursorToInputText(int promptLen, int inputLen)
    {
        var row = InputRow();
        var x = Math.Min(SafeLineWidth(), promptLen + inputLen);
        Console.SetCursorPosition(x, row);
    }

    private static void MoveCursorToInputRow()
    {
        Console.SetCursorPosition(0, InputRow());
    }

    private static void ClearMenu(int rowsRendered, int topRow)
    {
        if (rowsRendered <= 0 || topRow < 0) return;
        for (var i = 0; i < rowsRendered; i++)
        {
            var row = topRow + i;
            if (row < 0) continue;
            if (row >= Console.BufferHeight) break;
            Console.SetCursorPosition(0, row);
            Console.Write(new string(' ', SafeLineWidth()));
        }
    }

    private static void WriteHint()
    {
        var row = HintRow();
        if (row < 0) return;

        Console.SetCursorPosition(0, row);
        Console.Write(new string(' ', SafeLineWidth()));
        Console.SetCursorPosition(0, row);

        var hint = "Shortcuts: Tab complete  PgUp/PgDn scroll  Alt+1/2/3 tabs  Alt+4 view  Alt+5/6 worker  Ctrl+P/N history";
        if (hint.Length > SafeLineWidth())
            hint = hint[..SafeLineWidth()];

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write(hint);
        Console.ResetColor();
    }

    private static void ClearHint(bool wasVisible)
    {
        if (!wasVisible) return;
        var row = HintRow();
        if (row < 0) return;
        Console.SetCursorPosition(0, row);
        Console.Write(new string(' ', SafeLineWidth()));
    }

    private static int HintRow()
    {
        var inputRow = InputRow();
        return inputRow > 0 ? inputRow - 1 : -1;
    }

    private static void WriteFooterSeparator()
    {
        var row = FooterRow();
        if (row < 0) return;

        Console.SetCursorPosition(0, row);
        Console.Write(new string(' ', SafeLineWidth()));
        Console.SetCursorPosition(0, row);
        var line = new string('-', SafeLineWidth());
        if (line.Length > 0)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write(line);
            Console.ResetColor();
            Console.SetCursorPosition(1, row);
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.Write(" Input ");
            Console.ResetColor();
        }
    }

    private static void AddHistory(string submitted)
    {
        if (string.IsNullOrWhiteSpace(submitted)) return;
        if (s_history.Count > 0 &&
            string.Equals(s_history[^1], submitted, StringComparison.Ordinal))
            return;

        s_history.Add(submitted);
        if (s_history.Count > MaxHistory)
            s_history.RemoveRange(0, s_history.Count - MaxHistory);
    }

    private static void ApplyHistory(int delta, StringBuilder buffer, ref int cursor)
    {
        if (s_history.Count == 0) return;

        if (cursor < 0)
            cursor = s_history.Count;

        cursor = Math.Clamp(cursor + delta, 0, s_history.Count);
        buffer.Clear();

        if (cursor < s_history.Count)
            buffer.Append(s_history[cursor]);
    }

    private static int InputRow() => Math.Clamp(Console.WindowTop + Console.WindowHeight - 1, 0, Console.BufferHeight - 1);
    private static int FooterRow() => Math.Max(0, InputRow() - 2);
    private static int SafeLineWidth() => Math.Max(0, Console.WindowWidth - 1);
    private static string NormalizePrompt(string prompt) => Regex.Replace(prompt, @"\[[^\]]+\]", "").Trim();
}
