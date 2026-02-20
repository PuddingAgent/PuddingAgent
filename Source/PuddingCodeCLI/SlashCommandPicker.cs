using Spectre.Console;

namespace PuddingCodeCLI;

/// <summary>
/// Provides an interactive slash-command picker.
/// When the user types '/', a filterable menu appears below the prompt.
/// Arrow keys navigate, Enter selects, Escape/Backspace cancels.
/// </summary>
public static class SlashCommandPicker
{
    public record CommandEntry(string Command, string Description);

    private static readonly CommandEntry[] s_commands =
    [
        new("/help",            "Show all commands"),
        new("/open",            "Open a project directory"),
        new("/model",           "List / manage LLM providers"),
        new("/model add",       "Add a new LLM provider"),
        new("/model use",       "Switch active provider"),
        new("/model remove",    "Remove a provider"),
        new("/undo",            "Undo last N tool snapshots"),
        new("/snapshot",        "Create a manual snapshot"),
        new("/history",         "List recent snapshots"),
        new("/config",          "Show current config"),
        new("/swarm",           "Start a swarm session"),
        new("/swarm status",    "Show swarm worker status"),
        new("/swarm cancel",    "Cancel active swarm"),
        new("/exit",            "Exit Pudding"),
    ];

    /// <summary>
    /// Reads a line from the terminal with slash-command autocomplete.
    /// Returns the final input string (may be a command or a normal message).
    /// Returns null if the user pressed Ctrl+C.
    /// </summary>
    public static string? ReadLine(string prompt)
    {
        // Print prompt
        AnsiConsole.Markup(prompt + " ");

        var buffer = new System.Text.StringBuilder();
        var pickerActive = false;
        var pickerFilter = "";
        var pickerIndex = 0;
        CommandEntry[] filtered = [];

        // Save cursor position after prompt
        var (promptLeft, promptTop) = (Console.CursorLeft, Console.CursorTop);

        while (true)
        {
            ConsoleKeyInfo key;
            try { key = Console.ReadKey(intercept: true); }
            catch (InvalidOperationException) { return null; } // stdin redirected

            // Ctrl+C
            if (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control))
            {
                if (pickerActive) ClosePicker(filtered.Length);
                AnsiConsole.WriteLine();
                return null;
            }

            if (pickerActive)
            {
                switch (key.Key)
                {
                    case ConsoleKey.UpArrow:
                        pickerIndex = Math.Max(0, pickerIndex - 1);
                        RenderPicker(filtered, pickerIndex, buffer.ToString(), promptLeft, promptTop);
                        continue;

                    case ConsoleKey.DownArrow:
                        pickerIndex = Math.Min(filtered.Length - 1, pickerIndex + 1);
                        RenderPicker(filtered, pickerIndex, buffer.ToString(), promptLeft, promptTop);
                        continue;

                    case ConsoleKey.Enter:
                        if (filtered.Length > 0)
                        {
                            var selected = filtered[pickerIndex].Command;
                            ClosePicker(filtered.Length);
                            // Replace buffer with selected command
                            buffer.Clear();
                            buffer.Append(selected);
                            // Redraw input line
                            RedrawInputLine(buffer.ToString(), promptLeft, promptTop);
                            AnsiConsole.WriteLine();
                            return buffer.ToString();
                        }
                        // No matches — treat as normal Enter
                        goto case ConsoleKey.Enter; // unreachable but safe

                    case ConsoleKey.Escape:
                        ClosePicker(filtered.Length);
                        pickerActive = false;
                        pickerFilter = "";
                        pickerIndex = 0;
                        continue;

                    case ConsoleKey.Backspace:
                        if (buffer.Length > 0)
                        {
                            buffer.Remove(buffer.Length - 1, 1);
                            pickerFilter = buffer.ToString();

                            if (!pickerFilter.StartsWith('/'))
                            {
                                // Left the slash zone — close picker
                                ClosePicker(filtered.Length);
                                pickerActive = false;
                                filtered = [];
                            }
                            else
                            {
                                filtered = Filter(pickerFilter);
                                pickerIndex = 0;
                                RenderPicker(filtered, pickerIndex, buffer.ToString(), promptLeft, promptTop);
                            }
                        }
                        continue;

                    default:
                        if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
                        {
                            buffer.Append(key.KeyChar);
                            pickerFilter = buffer.ToString();
                            filtered = Filter(pickerFilter);
                            pickerIndex = 0;
                            RenderPicker(filtered, pickerIndex, buffer.ToString(), promptLeft, promptTop);
                        }
                        continue;
                }
            }

            // Normal (non-picker) input
            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    AnsiConsole.WriteLine();
                    return buffer.ToString();

                case ConsoleKey.Backspace:
                    if (buffer.Length > 0)
                    {
                        buffer.Remove(buffer.Length - 1, 1);
                        RedrawInputLine(buffer.ToString(), promptLeft, promptTop);
                    }
                    break;

                default:
                    if (key.KeyChar == '/' && buffer.Length == 0)
                    {
                        // Trigger picker
                        buffer.Append('/');
                        pickerFilter = "/";
                        filtered = Filter(pickerFilter);
                        pickerIndex = 0;
                        pickerActive = true;
                        RenderPicker(filtered, pickerIndex, buffer.ToString(), promptLeft, promptTop);
                    }
                    else if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
                    {
                        buffer.Append(key.KeyChar);
                        Console.Write(key.KeyChar);
                    }
                    break;
            }
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static CommandEntry[] Filter(string prefix) =>
        s_commands
            .Where(c => c.Command.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToArray();

    private static void RedrawInputLine(string text, int promptLeft, int promptTop)
    {
        Console.SetCursorPosition(promptLeft, promptTop);
        Console.Write(text + new string(' ', Math.Max(0, Console.WindowWidth - promptLeft - text.Length - 1)));
        Console.SetCursorPosition(promptLeft + text.Length, promptTop);
    }

    private static void RenderPicker(CommandEntry[] entries, int selectedIndex, string currentInput, int promptLeft, int promptTop)
    {
        // Redraw input line first
        RedrawInputLine(currentInput, promptLeft, promptTop);

        if (entries.Length == 0)
        {
            // Clear any previous picker lines
            ClearPickerLines(1, promptTop);
            return;
        }

        var maxVisible = Math.Min(entries.Length, 8);
        var startRow = promptTop + 1;

        for (var i = 0; i < maxVisible; i++)
        {
            var row = startRow + i;
            if (row >= Console.BufferHeight) break;
            Console.SetCursorPosition(0, row);
            Console.Write(new string(' ', Console.WindowWidth)); // clear line
            Console.SetCursorPosition(2, row);

            var entry = entries[i];
            var isSelected = i == selectedIndex;

            if (isSelected)
            {
                Console.BackgroundColor = ConsoleColor.DarkBlue;
                Console.ForegroundColor = ConsoleColor.White;
                Console.Write($" {entry.Command,-22} {entry.Description}");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($" {entry.Command,-22}");
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.Write($" {entry.Description}");
                Console.ResetColor();
            }
        }

        // Clear extra lines from previous render
        ClearPickerLines(maxVisible, promptTop, clearFrom: maxVisible);

        // Return cursor to input line
        Console.SetCursorPosition(promptLeft + currentInput.Length, promptTop);
    }

    private static void ClosePicker(int previousCount)
    {
        ClearPickerLines(previousCount, Console.CursorTop > 0 ? Console.CursorTop : 0);
    }

    private static void ClearPickerLines(int count, int promptTop, int clearFrom = 0)
    {
        var savedLeft = Console.CursorLeft;
        var savedTop = Console.CursorTop;

        for (var i = clearFrom; i < count + 2; i++)
        {
            var row = promptTop + 1 + i;
            if (row >= Console.BufferHeight) break;
            Console.SetCursorPosition(0, row);
            Console.Write(new string(' ', Console.WindowWidth));
        }

        Console.SetCursorPosition(savedLeft, savedTop);
    }
}
