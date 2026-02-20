using PuddingCode.Core;
using PuddingCode.Models;
using PuddingCode.Tools;
using PuddingCode.Swarm;
using PuddingCode.Abstractions;
using PuddingCodeCLI.Commands;
using Spectre.Console;

// ═══════════════════════ Header ═══════════════════════

AnsiConsole.Write(new FigletText("PuddingCode").Color(Color.Yellow));
AnsiConsole.MarkupLine("[grey]v0.1.0 - Agentic Self-Programming CLI[/]");
AnsiConsole.WriteLine();

// ═══════════════════════ Configuration ═══════════════════════

var configPath = ConfigManager.DefaultPath;
var config = ConfigManager.Load(configPath);

// Env-var override (for CI / Docker / quick test)
var envKey = Environment.GetEnvironmentVariable("PUDDING_API_KEY");
var usingEnv = !string.IsNullOrWhiteSpace(envKey);

ProviderEntry active;

if (usingEnv)
{
    active = new ProviderEntry
    {
        Id = "_env",
        Name = "Environment Variables",
        Endpoint = Environment.GetEnvironmentVariable("PUDDING_API_ENDPOINT")
                   ?? "https://api.openai.com/v1",
        ApiKey = envKey!,
        Model = Environment.GetEnvironmentVariable("PUDDING_MODEL") ?? "gpt-4o"
    };
    AnsiConsole.MarkupLine("[grey]Using config from PUDDING_API_KEY environment variable[/]");
}
else
{
    // First-time setup
    if (config.Providers.Count == 0)
    {
        AnsiConsole.MarkupLine("[yellow]🍮 Welcome! Let's set up your first LLM provider.[/]");
        AnsiConsole.MarkupLine($"[grey]Config → {configPath.EscapeMarkup()}[/]");
        AnsiConsole.WriteLine();

        var first = PromptNewProvider();
        config.Providers.Add(first);
        config.ActiveProvider = first.Id;
        ConfigManager.Save(configPath, config);

        AnsiConsole.MarkupLine($"\n[green]✓[/] Provider [yellow]{first.Id.EscapeMarkup()}[/] saved and activated.");

        while (AnsiConsole.Confirm("[grey]Add another provider?[/]", false))
        {
            var extra = PromptNewProvider();
            config.Providers.Add(extra);
            ConfigManager.Save(configPath, config);
            AnsiConsole.MarkupLine($"[green]✓[/] Provider [yellow]{extra.Id.EscapeMarkup()}[/] saved.");
        }

        AnsiConsole.WriteLine();
    }
    else
    {
        AnsiConsole.MarkupLine($"[grey]Config → {configPath.EscapeMarkup()}[/]");
    }

    active = config.Providers.Find(p => p.Id == config.ActiveProvider)
             ?? config.Providers[0];
}

AnsiConsole.MarkupLine($"[grey]Provider : {active.Id.EscapeMarkup()} — {active.Name.EscapeMarkup()}[/]");
AnsiConsole.MarkupLine($"[grey]Model    : {active.Model.EscapeMarkup()}[/]");
AnsiConsole.MarkupLine($"[grey]Endpoint : {active.Endpoint.EscapeMarkup()}[/]");
AnsiConsole.MarkupLine("[grey]Type /help for commands[/]");
AnsiConsole.WriteLine();

// ═══════════════════════ Initialize Agent ═══════════════════════

var httpClient = new HttpClient();
var project = new ProjectContext(Environment.CurrentDirectory);
var guard = new PermissionGuard(project.RootPath);
var registry = new ToolRegistry();
registry.Register(new FileTool(project, guard));
registry.Register(new ShellTool(project, guard));

var snapshot = new GitSnapshotService(project.RootPath);
if (snapshot.IsGitRepo)
    AnsiConsole.MarkupLine("[grey]Git repo detected — auto-snapshots enabled (/undo to rollback)[/]");
else
    AnsiConsole.MarkupLine("[grey]No Git repo — /undo disabled (run 'git init' to enable)[/]");

var gateway = CreateGateway(active);
var agent = new AgentOrchestrator(gateway, registry, project, snapshot);

// Initialize Swarm components (Phase 1 stubs)
IWorkerManager workerManager = new WorkerManager(project.RootPath);
ISwarmOrchestrator? swarmOrchestrator = null; // Will be instantiated when swarm starts
var swarmCommands = new SwarmCommands(swarmOrchestrator, workerManager, snapshot, project.RootPath);

AnsiConsole.MarkupLine($"[grey]Project  : {project.RootPath.EscapeMarkup()}[/]");
AnsiConsole.WriteLine();

// ═══════════════════════ REPL ═══════════════════════

while (true)
{
    var input = AnsiConsole.Prompt(
        new TextPrompt<string>("[bold yellow]Pudding >[/]").AllowEmpty());

    if (string.IsNullOrWhiteSpace(input)) continue;
    if (input.Equals("/exit", StringComparison.OrdinalIgnoreCase)) break;

    // Slash commands
    if (input.StartsWith('/'))
    {
        if (input.StartsWith("/swarm", StringComparison.OrdinalIgnoreCase))
        {
            swarmCommands.HandleCommand(input);
        }
        else
        {
            HandleCommand(input);
        }
        continue;
    }

    // Normal message → Agent
    var isStreamingReasoning = false;
    var isStreamingAnswer = false;

    await foreach (var evt in agent.ProcessAsync(input))
    {
        switch (evt)
        {
            case ThinkingEvent e:
                if (isStreamingReasoning || isStreamingAnswer) AnsiConsole.WriteLine();
                isStreamingReasoning = false;
                isStreamingAnswer = false;
                AnsiConsole.MarkupLine($"[grey italic]🍮 {e.Thought.EscapeMarkup()}[/]");
                break;

            case ReasoningEvent e:
                if (!isStreamingReasoning)
                {
                    AnsiConsole.Markup("[dim]💭 ");
                    isStreamingReasoning = true;
                }
                AnsiConsole.Markup($"[dim]{e.Delta.EscapeMarkup()}[/]");
                break;

            case StreamingAnswerEvent e:
                if (isStreamingReasoning)
                {
                    AnsiConsole.MarkupLine("[/]");   // close reasoning dim
                    isStreamingReasoning = false;
                }
                if (!isStreamingAnswer)
                {
                    AnsiConsole.WriteLine();
                    isStreamingAnswer = true;
                }
                AnsiConsole.Write(e.Delta);
                break;

            case ToolCallEvent e:
                if (isStreamingReasoning || isStreamingAnswer) AnsiConsole.WriteLine();
                isStreamingReasoning = false;
                isStreamingAnswer = false;
                AnsiConsole.MarkupLine($"[blue]⚙️  Calling tool: {e.ToolName.EscapeMarkup()}[/]");
                break;

            case ToolResultEvent e:
                var text = e.Result.Length > 2000
                    ? e.Result[..2000] + "\n… (truncated)"
                    : e.Result;
                AnsiConsole.Write(new Panel(text.EscapeMarkup())
                    .Header($"[green]📂 {e.ToolName.EscapeMarkup()} result[/]")
                    .Border(BoxBorder.Rounded)
                    .BorderColor(Color.Green));
                break;

            case AnswerEvent e:
                if (isStreamingAnswer)
                {
                    // Already streamed; just finalize with newline
                    AnsiConsole.WriteLine();
                    AnsiConsole.WriteLine();
                }
                else
                {
                    AnsiConsole.WriteLine();
                    AnsiConsole.MarkupLine(e.Content.EscapeMarkup());
                    AnsiConsole.WriteLine();
                }
                isStreamingAnswer = false;
                break;

            case ErrorEvent e:
                if (isStreamingReasoning || isStreamingAnswer) AnsiConsole.WriteLine();
                isStreamingReasoning = false;
                isStreamingAnswer = false;
                AnsiConsole.MarkupLine($"[red]❌ {e.Message.EscapeMarkup()}[/]");
                break;
        }
    }

    if (isStreamingReasoning || isStreamingAnswer) AnsiConsole.WriteLine();
}

AnsiConsole.MarkupLine("[grey]Bye! 🍮[/]");
return;

// ═══════════════════════ Local Functions ═══════════════════════

OpenAiLlmGateway CreateGateway(ProviderEntry provider) =>
    new(httpClient, new LlmOptions(
        provider.Endpoint, provider.ApiKey, provider.Model,
        provider.Temperature, provider.MaxTokens));

void SwitchProvider(ProviderEntry provider)
{
    active = provider;
    gateway = CreateGateway(provider);
    agent = new AgentOrchestrator(gateway, registry, project, snapshot);
}

// ──────── Command router ────────

void HandleCommand(string input)
{
    var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    var cmd = parts[0].ToLowerInvariant();

    switch (cmd)
    {
        case "/help":
            CmdHelp();
            break;
        case "/config":
            CmdConfig();
            break;
        case "/model":
            CmdModel(parts);
            break;
        case "/open":
            CmdOpen(parts);
            break;
        case "/undo":
            CmdUndo(parts).GetAwaiter().GetResult();
            break;
        case "/snapshot":
            CmdSnapshot(parts).GetAwaiter().GetResult();
            break;
        case "/history":
            CmdHistory().GetAwaiter().GetResult();
            break;
        default:
            AnsiConsole.MarkupLine($"[grey]Unknown command: {cmd.EscapeMarkup()}. Type /help[/]");
            break;
    }
}

void CmdHelp()
{
    var table = new Table().Border(TableBorder.None).HideHeaders()
        .AddColumn("cmd").AddColumn("desc");
    table.AddRow("[yellow]/help[/]", "Show this help");
    table.AddRow("[yellow]/open [path][/]", "Open a project directory (default: current dir)");
    table.AddRow("[yellow]/model[/]", "List configured providers");
    table.AddRow("[yellow]/model add[/]", "Add a new LLM provider");
    table.AddRow("[yellow]/model use <id>[/]", "Switch active provider");
    table.AddRow("[yellow]/model remove <id>[/]", "Remove a provider");
    table.AddRow("[yellow]/undo [N][/]", "Undo last N tool snapshots (default: 1)");
    table.AddRow("[yellow]/snapshot [label][/]", "Create a manual snapshot");
    table.AddRow("[yellow]/history[/]", "List recent snapshots");
    table.AddRow("[yellow]/config[/]", "Show current provider details");
    table.AddRow("[yellow]/swarm[/]", "Swarm mode commands");
    table.AddRow("[yellow]/swarm status[/]", "View active swarm status");
    table.AddRow("[yellow]/swarm cancel[/]", "Cancel active swarm and cleanup");
    table.AddRow("[yellow]/exit[/]", "Exit PuddingCode");
    AnsiConsole.Write(table);
}

void CmdConfig()
{
    AnsiConsole.MarkupLine($"[grey]Config file  : {configPath.EscapeMarkup()}[/]");
    AnsiConsole.MarkupLine($"[grey]Provider     : {active.Id.EscapeMarkup()} — {active.Name.EscapeMarkup()}[/]");
    AnsiConsole.MarkupLine($"[grey]Endpoint     : {active.Endpoint.EscapeMarkup()}[/]");
    AnsiConsole.MarkupLine($"[grey]Model        : {active.Model.EscapeMarkup()}[/]");
    if (active.Temperature.HasValue)
        AnsiConsole.MarkupLine($"[grey]Temperature  : {active.Temperature.Value}[/]");
    if (active.MaxTokens.HasValue)
        AnsiConsole.MarkupLine($"[grey]Max Tokens   : {active.MaxTokens.Value}[/]");
    AnsiConsole.MarkupLine($"[grey]Providers    : {config.Providers.Count} total[/]");
    AnsiConsole.MarkupLine($"[grey]Project      : {project.RootPath.EscapeMarkup()}[/]");
}

void CmdOpen(string[] parts)
{
    var path = parts.Length > 1
        ? string.Join(' ', parts.Skip(1))
        : Environment.CurrentDirectory;

    if (!Directory.Exists(path))
    {
        AnsiConsole.MarkupLine($"[red]Directory not found: {path.EscapeMarkup()}[/]");
        return;
    }

    project = new ProjectContext(path);
    guard = new PermissionGuard(project.RootPath);
    registry = new ToolRegistry();
    registry.Register(new FileTool(project, guard));
    registry.Register(new ShellTool(project, guard));
    snapshot = new GitSnapshotService(project.RootPath);
    agent = new AgentOrchestrator(gateway, registry, project, snapshot);
    AnsiConsole.MarkupLine($"[green]✓[/] Project opened: [yellow]{project.RootPath.EscapeMarkup()}[/]");
    AnsiConsole.MarkupLine("[grey]Conversation history has been reset.[/]");
    if (snapshot.IsGitRepo)
        AnsiConsole.MarkupLine("[grey]Git repo detected — auto-snapshots enabled[/]");
}

// ──────── Git snapshot commands (D06) ────────

async Task CmdUndo(string[] parts)
{
    if (!snapshot.IsGitRepo)
    {
        AnsiConsole.MarkupLine("[red]No Git repo. Run 'git init' in the project root to enable /undo.[/]");
        return;
    }

    var count = 1;
    if (parts.Length > 1 && int.TryParse(parts[1], out var n) && n > 0)
        count = n;

    var undone = await snapshot.UndoAsync(count);
    if (undone == 0)
        AnsiConsole.MarkupLine("[grey]Nothing to undo — no pudding snapshots found.[/]");
    else
        AnsiConsole.MarkupLine($"[green]✓[/] Undid [yellow]{undone}[/] snapshot(s). Changes are back in your working tree.");
}

async Task CmdSnapshot(string[] parts)
{
    if (!snapshot.IsGitRepo)
    {
        AnsiConsole.MarkupLine("[red]No Git repo. Run 'git init' in the project root first.[/]");
        return;
    }

    var label = parts.Length > 1
        ? string.Join(' ', parts.Skip(1))
        : "manual snapshot";

    var hash = await snapshot.CreateSnapshotAsync(label);
    if (hash is null)
        AnsiConsole.MarkupLine("[grey]Nothing to snapshot — no changes detected.[/]");
    else
        AnsiConsole.MarkupLine($"[green]✓[/] Snapshot [yellow]{hash}[/]: {label.EscapeMarkup()}");
}

async Task CmdHistory()
{
    if (!snapshot.IsGitRepo)
    {
        AnsiConsole.MarkupLine("[red]No Git repo.[/]");
        return;
    }

    var entries = await snapshot.ListSnapshotsAsync(10);
    if (entries.Count == 0)
    {
        AnsiConsole.MarkupLine("[grey]No pudding snapshots found.[/]");
        return;
    }

    var table = new Table()
        .Border(TableBorder.Rounded)
        .AddColumn("[grey]#[/]")
        .AddColumn("Hash")
        .AddColumn("Label")
        .AddColumn("Time");

    for (var i = 0; i < entries.Count; i++)
    {
        var e = entries[i];
        table.AddRow(
            $"[grey]{i + 1}[/]",
            $"[yellow]{e.ShortHash.EscapeMarkup()}[/]",
            e.Label.EscapeMarkup(),
            e.Timestamp.LocalDateTime.ToString("HH:mm:ss"));
    }

    AnsiConsole.Write(table);
}

void CmdModel(string[] parts)
{
    if (usingEnv)
    {
        AnsiConsole.MarkupLine("[grey]Provider managed via environment variables. Unset PUDDING_API_KEY to use config file.[/]");
        return;
    }

    var sub = parts.Length > 1 ? parts[1].ToLowerInvariant() : "list";

    switch (sub)
    {
        case "list":
            ModelList();
            break;
        case "add":
            ModelAdd();
            break;
        case "use" when parts.Length > 2:
            ModelUse(parts[2]);
            break;
        case "remove" when parts.Length > 2:
            ModelRemove(parts[2]);
            break;
        default:
            AnsiConsole.MarkupLine("[grey]Usage: /model [list | add | use <id> | remove <id>][/]");
            break;
    }
}

void ModelList()
{
    var table = new Table()
        .Border(TableBorder.Rounded)
        .AddColumn("")
        .AddColumn("ID")
        .AddColumn("Model")
        .AddColumn("Endpoint");

    foreach (var p in config.Providers)
    {
        var marker = p.Id == active.Id ? "[green]●[/]" : "[grey]○[/]";
        table.AddRow(marker, p.Id.EscapeMarkup(), p.Model.EscapeMarkup(), p.Endpoint.EscapeMarkup());
    }

    AnsiConsole.Write(table);
}

void ModelAdd()
{
    var p = PromptNewProvider();
    config.Providers.Add(p);
    ConfigManager.Save(configPath, config);
    AnsiConsole.MarkupLine($"[green]✓[/] Provider [yellow]{p.Id.EscapeMarkup()}[/] added.");
    AnsiConsole.MarkupLine($"[grey]Switch with: /model use {p.Id.EscapeMarkup()}[/]");
}

void ModelUse(string targetId)
{
    var target = config.Providers.Find(p =>
        p.Id.Equals(targetId, StringComparison.OrdinalIgnoreCase));

    if (target is null)
    {
        AnsiConsole.MarkupLine($"[red]Provider \"{targetId.EscapeMarkup()}\" not found. Use /model to list.[/]");
        return;
    }

    config.ActiveProvider = target.Id;
    ConfigManager.Save(configPath, config);
    SwitchProvider(target);
    AnsiConsole.MarkupLine($"[green]✓[/] Switched to [yellow]{target.Id.EscapeMarkup()}[/] ({target.Model.EscapeMarkup()})");
    AnsiConsole.MarkupLine("[grey]Conversation history has been reset.[/]");
}

void ModelRemove(string targetId)
{
    var idx = config.Providers.FindIndex(p =>
        p.Id.Equals(targetId, StringComparison.OrdinalIgnoreCase));

    if (idx < 0)
    {
        AnsiConsole.MarkupLine($"[red]Provider \"{targetId.EscapeMarkup()}\" not found.[/]");
        return;
    }

    if (config.Providers.Count == 1)
    {
        AnsiConsole.MarkupLine("[red]Cannot remove the only provider. Add another first.[/]");
        return;
    }

    var removed = config.Providers[idx];
    config.Providers.RemoveAt(idx);

    if (active.Id == removed.Id)
    {
        config.ActiveProvider = config.Providers[0].Id;
        SwitchProvider(config.Providers[0]);
        AnsiConsole.MarkupLine($"[grey]Active provider switched to \"{active.Id.EscapeMarkup()}\"[/]");
    }

    ConfigManager.Save(configPath, config);
    AnsiConsole.MarkupLine($"[green]✓[/] Provider \"{removed.Id.EscapeMarkup()}\" removed.");
}

// ──────── Interactive provider setup ────────

ProviderEntry PromptNewProvider()
{
    // 1. Template
    var template = AnsiConsole.Prompt(
        new SelectionPrompt<string>()
            .Title("[green]Provider template:[/]")
            .AddChoices("OpenAI", "DeepSeek", "Claude (via proxy)", "Custom endpoint"));

    var (defEndpoint, defModel, defId) = template switch
    {
        "OpenAI" => ("https://api.openai.com/v1", "gpt-4o", "openai"),
        "DeepSeek" => ("https://api.deepseek.com", "deepseek-chat", "deepseek"),
        "Claude (via proxy)" => ("", "claude-sonnet-4-20250514", "claude"),
        _ => ("", "", "")
    };

    AnsiConsole.WriteLine();

    // 2. Provider ID
    var idPrompt = new TextPrompt<string>(
            "[green]Provider ID[/] [grey](short name, e.g. gpt / ds / claude)[/]:")
        .Validate(val =>
        {
            if (string.IsNullOrWhiteSpace(val))
                return Spectre.Console.ValidationResult.Error("Cannot be empty");
            if (val.Contains(' '))
                return Spectre.Console.ValidationResult.Error("No spaces allowed");
            if (config.Providers.Any(p => p.Id.Equals(val, StringComparison.OrdinalIgnoreCase)))
                return Spectre.Console.ValidationResult.Error($"\"{val}\" already exists");
            return Spectre.Console.ValidationResult.Success();
        });

    if (!string.IsNullOrEmpty(defId)
        && !config.Providers.Any(p => p.Id.Equals(defId, StringComparison.OrdinalIgnoreCase)))
        idPrompt.DefaultValue(defId);

    var id = AnsiConsole.Prompt(idPrompt);

    // 3. Endpoint (gateway auto-appends /chat/completions if missing)
    var epPrompt = new TextPrompt<string>(
        "[green]API Base URL[/] [grey]( /chat/completions will be appended automatically)[/]:");
    if (!string.IsNullOrEmpty(defEndpoint))
        epPrompt.DefaultValue(defEndpoint);
    var ep = AnsiConsole.Prompt(epPrompt);

    // 4. API Key
    var key = AnsiConsole.Prompt(
        new TextPrompt<string>("[green]API Key[/]:").Secret());

    // 5. Model
    var modelPrompt = new TextPrompt<string>("[green]Model name[/]:");
    if (!string.IsNullOrEmpty(defModel))
        modelPrompt.DefaultValue(defModel);
    var mdl = AnsiConsole.Prompt(modelPrompt);

    // 6. Temperature (optional)
    var tempStr = AnsiConsole.Prompt(
        new TextPrompt<string>("[green]Temperature[/] [grey](0.0–2.0, Enter to skip = model default)[/]:")
            .AllowEmpty());
    double? temp = double.TryParse(tempStr, out var tv) ? tv : null;

    // 7. Max Output Tokens (optional)
    var maxStr = AnsiConsole.Prompt(
        new TextPrompt<string>(
            "[green]Max Output Tokens[/] [grey](output limit, NOT context window. e.g. 4096/8192. Enter to skip = model default)[/]:")
            .AllowEmpty());
    int? maxTok = int.TryParse(maxStr, out var mv) ? mv : null;

    var name = template == "Custom endpoint" ? id : template;

    return new ProviderEntry
    {
        Id = id,
        Name = name,
        Endpoint = ep,
        ApiKey = key,
        Model = mdl,
        Temperature = temp,
        MaxTokens = maxTok
    };
}
