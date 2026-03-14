using PuddingCode.Abstractions;
using PuddingCode.Core;
using PuddingCode.Models;
using PuddingCode.Swarm;
using PuddingCode.Tools;
using PuddingCodeCLI;
using PuddingCodeCLI.Commands;
using Spectre.Console;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

var configPath = ConfigManager.DefaultPath;
var workspaceRoot = Environment.CurrentDirectory;
var config = ConfigManager.Load(configPath, workspaceRoot);
if (OneShotCommandApp.TryRun(args, configPath, workspaceRoot))
    return;

AnsiConsole.Write(new FigletText("PuddingCode").Color(Color.Yellow));
AnsiConsole.MarkupLine("[grey]v0.1.0 - Agentic Self-Programming CLI[/]");
AnsiConsole.WriteLine();

var envKey = Environment.GetEnvironmentVariable("PUDDING_API_KEY");
var usingEnv = !string.IsNullOrWhiteSpace(envKey);

ProviderEntry active;
DualModelRuntimeConfig dualModelConfig;

if (usingEnv)
{
    active = new ProviderEntry
    {
        Id = "_env",
        Name = "Environment Variables",
        Endpoint = Environment.GetEnvironmentVariable("PUDDING_API_ENDPOINT") ?? "https://api.openai.com/v1",
        ApiKey = envKey!,
        Model = Environment.GetEnvironmentVariable("PUDDING_MODEL") ?? "gpt-4o"
    };

    dualModelConfig = new DualModelRuntimeConfig(
        Conscious: new RuntimeModelRef("_env", active.Model),
        Subconscious: new RuntimeModelRef(
            "_env",
            Environment.GetEnvironmentVariable("PUDDING_SUBCONSCIOUS_MODEL") ?? "gpt-4o-mini"),
        Policy: new SubconsciousPolicyConfig
        {
            Visible = string.Equals(
                Environment.GetEnvironmentVariable("PUDDING_SUBCONSCIOUS_VISIBLE"),
                "1",
                StringComparison.OrdinalIgnoreCase),
            Verbosity = "low",
            BudgetRatio = 0.15
        });

    AnsiConsole.MarkupLine("[grey]Using config from PUDDING_API_KEY environment variable[/]");
}
else
{
    if (config.Providers.Count == 0)
    {
        RunFirstLaunchOnboarding();

        AnsiConsole.WriteLine();
        config = ConfigManager.Load(configPath, workspaceRoot);
    }
    else
    {
        AnsiConsole.MarkupLine($"[grey]Config -> {configPath.EscapeMarkup()}[/]");
    }

    active = config.Providers.Find(p => p.Id == config.ActiveProvider)
             ?? config.Providers[0];

    dualModelConfig = DualModelResolver.ResolveForRole(config, "spirit");
}

AnsiConsole.MarkupLine($"[grey]Provider : {active.Id.EscapeMarkup()} - {active.Name.EscapeMarkup()}[/]");
AnsiConsole.MarkupLine($"[grey]Model    : {active.Model.EscapeMarkup()}[/]");
AnsiConsole.MarkupLine($"[grey]Conscious: {dualModelConfig.Conscious.ProviderId.EscapeMarkup()} / {dualModelConfig.Conscious.Model.EscapeMarkup()}[/]");
AnsiConsole.MarkupLine($"[grey]Subconscious: {dualModelConfig.Subconscious.ProviderId.EscapeMarkup()} / {dualModelConfig.Subconscious.Model.EscapeMarkup()}[/]");
AnsiConsole.MarkupLine($"[grey]Endpoint : {active.Endpoint.EscapeMarkup()}[/]");
AnsiConsole.MarkupLine("[grey]Type /help for commands[/]");
if (config.WorkspaceYaml is { Enabled: true } ws)
{
    AnsiConsole.MarkupLine($"[grey]Workspace YAML: providers={ws.ProvidersLoaded}, models={ws.ModelsLoaded}, files={ws.ProviderFiles}[/]");
    foreach (var w in ws.Warnings.Take(3))
        AnsiConsole.MarkupLine($"[yellow]YAML warning:[/] {w.EscapeMarkup()}");
}
ShowConfigHealth();
AnsiConsole.WriteLine();

var httpClient = new HttpClient();
var project = new ProjectContext(Environment.CurrentDirectory);
var guard = new PermissionGuard(project.RootPath);
var centralLockManager = new CentralLockManager(project.RootPath);
var registry = new ToolRegistry();
registry.Register(new FileTool(project, guard, centralLockManager));
registry.Register(new ShellTool(project, guard));

var snapshot = new GitSnapshotService(project.RootPath);
var subconscious = CreateSubconsciousEngine(project.RootPath);
var todoStore = new TodoListStore(project.RootPath);
var reviewStore = new ReviewQueueStore(project.RootPath);
var subconsciousVisible = dualModelConfig.Policy.Visible;

if (snapshot.IsGitRepo)
    AnsiConsole.MarkupLine("[grey]Git repo detected - auto-snapshots enabled (/undo to rollback)[/]");
else
    AnsiConsole.MarkupLine("[grey]No Git repo - /undo disabled (run 'git init' to enable)[/]");

var gateway = CreateGateway(active);
var hookRuntime = HookRegistry.Build(project.RootPath, config.Hooks);
var hookMetrics = hookRuntime.Metrics;
var contextBudget = BuildContextBudget();
var agent = new AgentOrchestrator(
    gateway,
    registry,
    project,
    snapshot,
    hooks: hookRuntime.Hooks,
    contextBudget: contextBudget);

IWorkerManager workerManager = new WorkerManager(project.RootPath);
IContractManager contractManager = new ContractManager(project.RootPath);
var swarmCommands = new SwarmCommands(
    orchestratorFactory: () => new SwarmOrchestrator(
        contractManager,
        workerManager,
        finalTestCommand: config.Swarm?.FinalTestCommand),
    workerManager: workerManager,
    snapshotService: snapshot,
    projectRoot: project.RootPath);

var sessionStartedAt = DateTimeOffset.Now;
long sessionInputChars = 0;
long sessionOutputChars = 0;
long sessionInputTokens = 0;
long sessionOutputTokens = 0;
int sessionTurns = 0;
int sessionLlmRequests = 0;
int sessionToolCalls = 0;
int sessionToolResults = 0;
int sessionErrors = 0;
string lastUserInput = "-";
string lastTool = "-";
string lastError = "-";
string lastAnswer = "-";
string latestAssistantOutput = "-";
var interactionStream = new List<string>();
var uiPinnedLayout = true;
var interactionScrollOffset = 0;
var uiNeedsRender = true;
var uiLastRenderAt = DateTimeOffset.MinValue;
var uiLastWindowWidth = GetSafeWindowWidth();
var uiLastWindowHeight = GetSafeWindowHeight();
const int uiMinRenderIntervalMs = 120;
var uiLeftTabs = new[] { "main", "swarm", "todo" };
var uiLeftTabIndex = 0;
var uiCenterViews = new[] { "stream", "swarm", "todo", "review" };
var uiCenterViewIndex = 0;
string? uiSwarmFocusWorkerId = null;
var reviewDecision = "pending";
var reviewSummary = "No review snapshot.";
var reviewPreviewLines = new List<string> { "Run /review diff to generate review preview." };
DateTimeOffset? reviewUpdatedAt = null;
int? reviewSelectedId = null;
var latestReview = reviewStore.GetLatest();
if (latestReview is not null)
    ApplyReviewItem(latestReview, select: true);

AnsiConsole.MarkupLine($"[grey]Project  : {project.RootPath.EscapeMarkup()}[/]");
AnsiConsole.WriteLine();

while (true)
{
    RenderUiStatusBar(force: true);
    var input = SlashCommandPicker.ReadLine("[bold yellow]Pudding >[/]");

    if (input is null) break;
    if (string.IsNullOrWhiteSpace(input)) continue;
    if (input.Equals("/exit", StringComparison.OrdinalIgnoreCase)) break;

    if (input.StartsWith('/'))
    {
        AddInteraction($"[CMD] {input}");
        if (input.StartsWith("/swarm", StringComparison.OrdinalIgnoreCase))
            swarmCommands.HandleCommand(input);
        else
            HandleCommand(input);

        continue;
    }

    sessionTurns++;
    sessionInputChars += input.Length;
    sessionInputTokens += TokenEstimator.Estimate(input);
    lastUserInput = Clip(input, 64);
    AddInteraction($"[USER] {Clip(input, 80)}");
    await subconscious.RecordConsciousAsync($"USER: {input}");
    var subconsciousTask = subconscious.GenerateSignalAsync(input);
    var subconsciousEmitted = false;

    var isStreamingReasoning = false;
    var isStreamingAnswer = false;

    await foreach (var evt in agent.ProcessAsync(input))
    {
        if (!subconsciousEmitted && subconsciousTask.IsCompletedSuccessfully)
        {
            subconsciousEmitted = true;
            await EmitSubconsciousAsync(subconsciousTask.Result);
        }

        switch (evt)
        {
            case ThinkingEvent e:
                sessionLlmRequests++;
                AddInteraction($"[THINK] {Clip(e.Thought, 90)}");
                await subconscious.RecordConsciousAsync($"THINK: {e.Thought}");
                if (!uiPinnedLayout && (isStreamingReasoning || isStreamingAnswer)) AnsiConsole.WriteLine();
                isStreamingReasoning = false;
                isStreamingAnswer = false;
                if (!uiPinnedLayout)
                    AnsiConsole.MarkupLine($"[grey italic]馃嵁 {e.Thought.EscapeMarkup()}[/]");
                break;

            case ReasoningEvent e:
                if (!uiPinnedLayout && !isStreamingReasoning)
                {
                    AnsiConsole.Write(new Text("馃 ", new Style(Color.Grey)));
                    isStreamingReasoning = true;
                }
                if (!uiPinnedLayout)
                    AnsiConsole.Write(new Text(e.Delta, new Style(Color.Grey)));
                break;

            case StreamingAnswerEvent e:
                if (!uiPinnedLayout && isStreamingReasoning)
                {
                    AnsiConsole.WriteLine();
                    isStreamingReasoning = false;
                }
                if (!uiPinnedLayout && !isStreamingAnswer)
                {
                    AnsiConsole.WriteLine();
                    isStreamingAnswer = true;
                }
                if (!uiPinnedLayout)
                    AnsiConsole.Write(e.Delta);
                break;

            case ToolCallEvent e:
                sessionToolCalls++;
                lastTool = e.ToolName;
                AddInteraction($"[TOOL] call {e.ToolName}");
                await subconscious.RecordConsciousAsync($"TOOL CALL: {e.ToolName}");
                if (!uiPinnedLayout && (isStreamingReasoning || isStreamingAnswer)) AnsiConsole.WriteLine();
                isStreamingReasoning = false;
                isStreamingAnswer = false;
                if (!uiPinnedLayout)
                    AnsiConsole.MarkupLine($"[blue]鈿欙笍  Calling tool: {e.ToolName.EscapeMarkup()}[/]");
                break;

            case ToolResultEvent e:
                sessionToolResults++;
                AddInteraction($"[TOOL] result {e.ToolName}");
                await subconscious.RecordConsciousAsync($"TOOL RESULT: {e.ToolName}");
                var text = e.Result.Length > 2000
                    ? e.Result[..2000] + "\n... (truncated)"
                    : e.Result;
                if (!uiPinnedLayout)
                {
                    AnsiConsole.Write(new Panel(text.EscapeMarkup())
                        .Header($"[green]馃搨 {e.ToolName.EscapeMarkup()} result[/]")
                        .Border(BoxBorder.Rounded)
                        .BorderColor(Color.Green));
                }
                break;

            case AnswerEvent e:
                sessionOutputChars += e.Content?.Length ?? 0;
                sessionOutputTokens += TokenEstimator.Estimate(e.Content);
                lastAnswer = Clip(e.Content ?? "", 72);
                latestAssistantOutput = Clip(e.Content ?? "", 700);
                AddInteraction($"[ANS] {Clip(e.Content ?? "", 90)}");
                await subconscious.RecordConsciousAsync($"ANSWER: {e.Content}");
                if (!uiPinnedLayout && isStreamingAnswer)
                {
                    AnsiConsole.WriteLine();
                    AnsiConsole.WriteLine();
                }
                else if (!uiPinnedLayout)
                {
                    AnsiConsole.WriteLine();
                    RenderAssistantContent(e.Content);
                    AnsiConsole.WriteLine();
                }
                isStreamingAnswer = false;
                break;

            case ErrorEvent e:
                sessionErrors++;
                lastError = Clip(e.Message, 72);
                AddInteraction($"[ERR] {Clip(e.Message, 90)}");
                await subconscious.RecordConsciousAsync($"ERROR: {e.Message}");
                if (!uiPinnedLayout && (isStreamingReasoning || isStreamingAnswer)) AnsiConsole.WriteLine();
                isStreamingReasoning = false;
                isStreamingAnswer = false;
                if (!uiPinnedLayout)
                    AnsiConsole.MarkupLine($"[red]鉂?{e.Message.EscapeMarkup()}[/]");
                break;
        }

        if (uiPinnedLayout)
            RenderUiStatusBar();
    }

    if (!subconsciousEmitted)
    {
        var completed = await Task.WhenAny(subconsciousTask, Task.Delay(50));
        if (completed == subconsciousTask && subconsciousTask.IsCompletedSuccessfully)
            await EmitSubconsciousAsync(subconsciousTask.Result);
    }

    if (!uiPinnedLayout && (isStreamingReasoning || isStreamingAnswer)) AnsiConsole.WriteLine();
}

AnsiConsole.MarkupLine("[grey]Bye! 馃嵁[/]");
return;

void RenderUiStatusBar(bool force = false)
{
    if (!uiPinnedLayout)
        return;

    var now = DateTimeOffset.UtcNow;
    var currentWidth = GetSafeWindowWidth();
    var currentHeight = GetSafeWindowHeight();
    var sizeChanged = uiLastWindowWidth != currentWidth || uiLastWindowHeight != currentHeight;
    if (!force && !uiNeedsRender && !sizeChanged)
        return;

    if (!force && !sizeChanged && (now - uiLastRenderAt).TotalMilliseconds < uiMinRenderIntervalMs)
        return;

    try
    {
        AnsiConsole.Clear();
    }
    catch
    {
        uiPinnedLayout = false;
        return;
    }
    uiLastWindowWidth = currentWidth;
    uiLastWindowHeight = currentHeight;
    uiLastRenderAt = now;
    uiNeedsRender = false;

    var projectName = Path.GetFileName(project.RootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    if (string.IsNullOrWhiteSpace(projectName))
        projectName = project.RootPath;

    var uptime = DateTimeOffset.Now - sessionStartedAt;
    var tokenEstimate = Math.Max(0, sessionInputTokens + sessionOutputTokens);
    var contextEstimate = $"{Math.Max(0, sessionInputTokens)}/{Math.Max(8192, active.MaxTokens ?? 8192)}";
    var costEstimate = BillingEstimator.FormatCost(
        active.Billing,
        sessionInputTokens,
        sessionOutputTokens,
        sessionLlmRequests,
        sessionTurns);
    var workerCount = workerManager.GetActiveWorkers().Count;
    var todoItems = todoStore.List();
    var todoOpen = todoItems.Count(t => !t.Done);
    var todoDone = todoItems.Count - todoOpen;

    var centerView = uiCenterViews[Math.Clamp(uiCenterViewIndex, 0, uiCenterViews.Length - 1)];
    var paneLineBudget = Math.Max(8, currentHeight - 6);
    var left = BuildCenterPane(centerView, paneLineBudget);
    var right = BuildRightPane(
        tokenEstimate,
        contextEstimate,
        costEstimate,
        workerCount,
        todoOpen,
        todoDone);
    var centerHeader = $"[bold]Stream[/] [grey]({centerView})[/]";
    if (centerView == "swarm" && !string.IsNullOrWhiteSpace(uiSwarmFocusWorkerId))
        centerHeader = $"[bold]Stream[/] [grey](swarm:{Clip(uiSwarmFocusWorkerId, 18).EscapeMarkup()})[/]";

    var statusWidth = Math.Clamp(currentWidth / 4, 28, 42);
    var layout = new Layout("root")
        .SplitColumns(
            new Layout("main"),
            new Layout("status").Size(statusWidth));

    layout["main"].Update(
        new Panel(left)
            .Header(centerHeader)
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Blue)
            .Expand());
    layout["status"].Update(
        new Panel(right)
            .Header("[bold]Status[/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Grey)
            .Expand());

    AnsiConsole.Write(layout);
}

Grid BuildCenterPane(string centerView, int lineBudget)
{
    var lines = new List<string>();
    if (centerView == "stream")
    {
        var total = interactionStream.Count;
        var windowSize = GetInteractionWindowSize();
        var maxOffset = Math.Max(0, total - windowSize);
        interactionScrollOffset = Math.Clamp(interactionScrollOffset, 0, maxOffset);
        var startIndex = Math.Max(0, total - windowSize - interactionScrollOffset);
        var visible = interactionStream.Skip(startIndex).Take(windowSize).ToList();

        lines.Add("[bold]Stream[/]");
        lines.Add($"[grey]rows {startIndex + 1}-{startIndex + visible.Count}/{Math.Max(1, total)}  offset={interactionScrollOffset}[/]");
        if (visible.Count == 0)
        {
            lines.Add("[grey]No interaction yet. Start by typing a message or /help.[/]");
        }
        else
        {
            foreach (var line in visible)
                lines.Add($"[grey]{Clip(line, 72).EscapeMarkup()}[/]");
        }

        lines.Add("");
        lines.Add("[bold]Latest Output[/]");
        lines.Add($"[grey]{Clip(latestAssistantOutput, 220).EscapeMarkup()}[/]");
    }
    else if (centerView == "swarm")
    {
        var workers = workerManager.GetActiveWorkers()
            .OrderBy(w => w.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var session = LoadLatestSwarmSessionState();
        var tasks = session?.Tasks ?? [];
        lines.Add("[bold]Swarm Workers[/]");
        lines.Add($"[grey]count={workers.Count}[/]");
        if (workers.Count == 0)
        {
            uiSwarmFocusWorkerId = null;
            lines.Add("[grey]No active workers. Run /swarm <task> first.[/]");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(uiSwarmFocusWorkerId) || workers.All(w => !w.Id.Equals(uiSwarmFocusWorkerId, StringComparison.OrdinalIgnoreCase)))
                uiSwarmFocusWorkerId = workers[0].Id;

            foreach (var worker in workers.Take(Math.Max(1, lineBudget - 8)))
            {
                var marker = worker.Id.Equals(uiSwarmFocusWorkerId, StringComparison.OrdinalIgnoreCase)
                    ? "[yellow]>[/]"
                    : "[grey]-[/]";
                lines.Add($"{marker} [grey]{Clip(worker.Id, 18).EscapeMarkup()}[/] {Clip(worker.Role.ToString(), 10).EscapeMarkup()}");
                lines.Add($"   [grey]{Clip(worker.Name, 56).EscapeMarkup()}[/]");
            }
        }

        lines.Add("");
        if (tasks.Count > 0)
        {
            var grouped = tasks
                .GroupBy(t => t.Status)
                .OrderBy(g => g.Key.ToString(), StringComparer.OrdinalIgnoreCase)
                .Select(g => $"{g.Key}:{g.Count()}")
                .ToArray();
            lines.Add("[bold]Task Summary[/]");
            lines.Add($"[grey]{Clip(string.Join("  ", grouped), 72).EscapeMarkup()}[/]");

            if (!string.IsNullOrWhiteSpace(uiSwarmFocusWorkerId))
            {
                var focused = tasks
                    .Where(t => t.AssignedTo != null && t.AssignedTo.Equals(uiSwarmFocusWorkerId, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                lines.Add($"[bold]Focused Tasks[/] [grey]({focused.Count})[/]");
                if (focused.Count == 0)
                {
                    lines.Add("[grey]No tasks assigned to focused worker.[/]");
                }
                else
                {
                    foreach (var task in focused.Take(Math.Max(1, lineBudget - lines.Count - 4)))
                    {
                        lines.Add($"{TaskStatusMarkup(task.Status)} [grey]{Clip(task.Id, 12).EscapeMarkup()}[/] {Clip(task.Title, 44).EscapeMarkup()}");
                    }
                }
            }
        }
        else
        {
            lines.Add("[bold]Task Summary[/]");
            lines.Add("[grey]No task state found. Run /swarm <task> first.[/]");
        }

        lines.Add("");
        lines.Add("[bold]Control[/]");
        lines.Add("[grey]/ui worker next|prev|clear|<id>[/]");
    }
    else
    {
        if (centerView == "todo")
        {
            var items = todoStore.List();
            lines.Add("[bold]Todo Board[/]");
            lines.Add($"[grey]total={items.Count}[/]");
            if (items.Count == 0)
            {
                lines.Add("[grey]No todo items. Use /todo add <text>[/]");
            }
            else
            {
                foreach (var item in items.Take(Math.Max(1, lineBudget - 6)))
                {
                    var state = item.Done ? "[green]done[/]" : "[yellow]open[/]";
                    lines.Add($"{state} [grey]#{item.Id}[/] {Clip(item.Title, 62).EscapeMarkup()}");
                }
            }
        }
        else
        {
            var queueCount = reviewStore.List().Count;
            lines.Add("[bold]Diff Review[/]");
            lines.Add($"[grey]Queue[/] {queueCount} item(s)");
            lines.Add($"[grey]Selected[/] {(reviewSelectedId?.ToString() ?? "-")}");
            var decisionColor = reviewDecision switch
            {
                "approved" => "green",
                "rejected" => "red",
                _ => "yellow"
            };
            lines.Add($"[grey]Decision[/] [{decisionColor}]{reviewDecision}[/]");
            lines.Add($"[grey]Summary[/] {Clip(reviewSummary, 68).EscapeMarkup()}");
            lines.Add($"[grey]Updated[/] {(reviewUpdatedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "-")}");
            if (reviewSelectedId is not null)
            {
                var selected = reviewStore.Get(reviewSelectedId.Value);
                if (selected is not null && !string.IsNullOrWhiteSpace(selected.ApprovedSnapshotHash))
                    lines.Add($"[grey]Snapshot[/] {Clip(selected.ApprovedSnapshotHash, 40).EscapeMarkup()}");
                if (selected is not null && !string.IsNullOrWhiteSpace(selected.Note))
                    lines.Add($"[grey]Note[/] {Clip(selected.Note, 58).EscapeMarkup()}");
            }
            lines.Add("");
            lines.Add("[bold]Preview[/]");
            foreach (var row in reviewPreviewLines.Take(Math.Max(1, lineBudget - lines.Count - 4)))
                lines.Add($"[grey]{Clip(row, 74).EscapeMarkup()}[/]");
            lines.Add("");
            lines.Add("[bold]Control[/]");
            lines.Add("[grey]/review diff|list|use <id>|approve|reject|reset[/]");
            lines.Add("[grey]/review approve apply[/]");
            lines.Add("[grey]/review reject discard [--yes][/]");
        }
    }

    var limited = lines.Take(lineBudget).ToList();
    var grid = new Grid().AddColumn(new GridColumn());
    foreach (var line in limited)
        grid.AddRow(line);
    return grid;
}

Grid BuildRightPane(long tokenEstimate, string contextEstimate, string costEstimate, int workerCount, int todoOpen, int todoDone)
{
    var grid = new Grid()
        .AddColumn(new GridColumn().NoWrap())
        .AddColumn(new GridColumn());

    void Add(string label, string value) => grid.AddRow(label, Clip(value, 50).EscapeMarkup());

    Add("[grey]Turns[/]", sessionTurns.ToString());
    Add("[grey]Token est.[/]", tokenEstimate.ToString());
    Add("[grey]Context est.[/]", contextEstimate);
    Add("[grey]Cost est.[/]", costEstimate);
    Add("[grey]Billing[/]", active.Billing.Mode.ToString());
    Add("[grey]Hooks[/]", $"{hookMetrics.PreToolCalls}/{hookMetrics.PostToolCalls}/{hookMetrics.PreReplies}/{hookMetrics.PostReplies}");
    Add("[grey]Tool calls[/]", $"{sessionToolCalls}/{sessionToolResults}");
    Add("[grey]Errors[/]", sessionErrors.ToString());
    Add("[grey]Active workers[/]", workerCount.ToString());
    Add("[grey]Todo[/]", $"{todoOpen} open / {todoDone} done");
    Add("[grey]Last input[/]", lastUserInput);
    Add("[grey]Last tool[/]", lastTool);
    Add("[grey]Last error[/]", lastError);
    Add("[grey]Last answer[/]", lastAnswer);
    Add("[grey]Tips[/]", "PgUp/PgDn scroll  Alt+[ ] tab  Alt+4 view  Alt+5/6 worker  Alt+7 clear  Alt+8 review");

    return grid;
}

void AddInteraction(string line)
{
    if (string.IsNullOrWhiteSpace(line)) return;

    var windowSize = GetInteractionWindowSize();
    var previousCount = interactionStream.Count;
    var previousMaxOffset = Math.Max(0, previousCount - windowSize);
    var wasFollowingTail = interactionScrollOffset == 0;

    interactionStream.Add(line);
    if (interactionStream.Count > 200)
        interactionStream.RemoveRange(0, interactionStream.Count - 200);

    var currentMaxOffset = Math.Max(0, interactionStream.Count - windowSize);
    if (wasFollowingTail)
    {
        interactionScrollOffset = 0;
    }
    else
    {
        var drift = Math.Max(0, currentMaxOffset - previousMaxOffset);
        interactionScrollOffset = Math.Clamp(interactionScrollOffset + drift, 0, currentMaxOffset);
    }

    uiNeedsRender = true;
}

int GetInteractionWindowSize()
{
    var paneLineBudget = Math.Max(8, GetSafeWindowHeight() - 6);
    return Math.Max(6, Math.Min(paneLineBudget - 6, 18));
}

static int GetSafeWindowWidth()
{
    try
    {
        return Math.Max(80, Console.WindowWidth);
    }
    catch
    {
        return 120;
    }
}

static int GetSafeWindowHeight()
{
    try
    {
        return Math.Max(20, Console.WindowHeight);
    }
    catch
    {
        return 40;
    }
}

static string Clip(string value, int max)
{
    if (string.IsNullOrWhiteSpace(value)) return "-";
    var text = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
    return text.Length <= max ? text : text[..max] + "...";
}

void RenderAssistantContent(string? content)
{
    if (string.IsNullOrWhiteSpace(content))
        return;

    MarkdownConsoleRenderer.Render(content);
}

OpenAiLlmGateway CreateGateway(ProviderEntry provider) =>
    new(httpClient, new LlmOptions(
        provider.Endpoint,
        provider.ApiKey,
        provider.Model,
        provider.Temperature,
        provider.MaxTokens));

SubconsciousEngine CreateSubconsciousEngine(string projectRoot)
{
    var mm = config.MemoryMaintenance;
    return new SubconsciousEngine(projectRoot, new MemoryMaintenanceOptions(
        CompactWriteThreshold: mm.CompactWriteThreshold,
        CompactMinEntries: mm.CompactMinEntries,
        CompactKeepEntries: mm.CompactKeepEntries,
        UseModelSummarization: mm.UseModelSummarization,
        ModelSummaryDailyTokenBudget: mm.ModelSummaryDailyTokenBudget,
        ModelSummaryMaxInputChars: mm.ModelSummaryMaxInputChars,
        ModelSummaryMaxOutputChars: mm.ModelSummaryMaxOutputChars,
        ModelSummarizer: SummarizeWithSubconsciousModelAsync));
}

ProviderEntry ResolveSubconsciousProvider()
{
    if (usingEnv)
        return active;

    var target = config.Providers.FirstOrDefault(p =>
        p.Id.Equals(dualModelConfig.Subconscious.ProviderId, StringComparison.OrdinalIgnoreCase));
    return target ?? active;
}

async Task<string?> SummarizeWithSubconsciousModelAsync(string prompt, int maxOutputChars, CancellationToken ct)
{
    var provider = ResolveSubconsciousProvider();
    var maxTokens = Math.Clamp(maxOutputChars / 4, 128, 2048);
    var summaryGateway = new OpenAiLlmGateway(httpClient, new LlmOptions(
        provider.Endpoint,
        provider.ApiKey,
        dualModelConfig.Subconscious.Model,
        0.2,
        maxTokens));

    var messages =
        new List<ChatMessage>
        {
            new(ChatRole.System,
                "You are a memory distiller. Return only concise bullet lines prefixed with '- '."),
            new(ChatRole.User, prompt)
        };

    var resp = await summaryGateway.ChatAsync(messages, [], ct);
    var text = resp.Content?.Trim();
    if (string.IsNullOrWhiteSpace(text)) return null;
    return text.Length > maxOutputChars ? text[..maxOutputChars] : text;
}

void SwitchProvider(ProviderEntry provider)
{
    active = provider;
    gateway = CreateGateway(provider);
    hookRuntime = HookRegistry.Build(project.RootPath, config.Hooks);
    hookMetrics = hookRuntime.Metrics;
    contextBudget = BuildContextBudget();
    agent = new AgentOrchestrator(
        gateway,
        registry,
        project,
        snapshot,
        hooks: hookRuntime.Hooks,
        contextBudget: contextBudget);

    if (!usingEnv)
        dualModelConfig = DualModelResolver.ResolveForRole(config, "spirit");
}

async Task EmitSubconsciousAsync(SubconsciousSignal signal)
{
    await subconscious.RecordSubconsciousAsync($"Recall={signal.Recall}; Risk={signal.Risk}; Boundary={signal.BoundaryCheck}");
    await subconscious.PersistMemoryCandidateAsync(signal.MemoryWriteCandidate);
    AddInteraction($"[SUB] recall {Clip(signal.Recall, 72)}");

    if (!subconsciousVisible) return;

    if (!uiPinnedLayout)
    {
        AnsiConsole.MarkupLine($"[grey][[S]] Recall: {signal.Recall.EscapeMarkup()}[/]");
        AnsiConsole.MarkupLine($"[grey][[S]] Risk: {signal.Risk.EscapeMarkup()}[/]");
        AnsiConsole.MarkupLine($"[grey][[S]] Boundary: {signal.BoundaryCheck.EscapeMarkup()}[/]");
    }
}

void HandleCommand(string input)
{
    // Use System.CommandLine token splitter so quoted args behave consistently.
    var parts = CommandLineStringSplitter.Instance.Split(input).ToArray();
    if (parts.Length == 0)
        return;

    var cmd = parts[0].ToLowerInvariant();

    switch (cmd)
    {
        case "/help":
            CmdHelp();
            break;
        case "/config":
            if (parts.Length > 1 && parts[1].Equals("check", StringComparison.OrdinalIgnoreCase))
            {
                ShowConfigHealth(forceShowOk: true);
            }
            else if (parts.Length > 1 && parts[1].Equals("fix", StringComparison.OrdinalIgnoreCase))
            {
                CmdConfigFix();
            }
            else
            {
                CmdConfig();
            }
            break;
        case "/model":
        case "/provider":
            if (!TryHandleProviderInteractive(input))
                AnsiConsole.MarkupLine("[grey]Usage: /model|/provider [list | add | use <id> | remove <id>][/]");
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
        case "/debug":
            CmdDebug(parts);
            break;
        case "/memory":
            CmdMemory(parts).GetAwaiter().GetResult();
            break;
        case "/prompt":
            CmdPrompt(parts);
            break;
        case "/hook":
            CmdHook(parts);
            break;
        case "/status":
            CmdStatus();
            break;
        case "/locks":
            CmdLocks(parts);
            break;
        case "/todo":
            CmdTodo(parts);
            break;
        case "/ui":
            CmdUi(parts);
            break;
        case "/review":
            CmdReview(parts).GetAwaiter().GetResult();
            break;
        case "/clear":
            CmdClear();
            break;
        default:
            AnsiConsole.MarkupLine($"[grey]Unknown command: {cmd.EscapeMarkup()}. Type /help[/]");
            break;
    }
}

bool TryHandleProviderInteractive(string rawInput)
{
    if (usingEnv)
    {
        AnsiConsole.MarkupLine("[grey]Provider managed via environment variables. Unset PUDDING_API_KEY to use config file.[/]");
        return true;
    }

    var normalized = rawInput.TrimStart();
    if (normalized.StartsWith("/model", StringComparison.OrdinalIgnoreCase))
        normalized = "provider" + normalized["/model".Length..];
    else if (normalized.StartsWith("/provider", StringComparison.OrdinalIgnoreCase))
        normalized = "provider" + normalized["/provider".Length..];
    else
        return false;

    var root = new RootCommand();
    var provider = new Command("provider");
    root.Add(provider);

    var list = new Command("list");
    list.SetHandler(() =>
    {
        ModelList();
    });
    provider.Add(list);

    var add = new Command("add");
    add.SetHandler(() =>
    {
        ModelAdd();
    });
    provider.Add(add);

    var idArg = new Argument<string>("id");

    var use = new Command("use");
    use.AddArgument(idArg);
    use.SetHandler((string id) =>
    {
        ModelUse(id);
    }, idArg);
    provider.Add(use);

    var remove = new Command("remove");
    remove.AddArgument(idArg);
    remove.SetHandler((string id) =>
    {
        ModelRemove(id);
    }, idArg);
    provider.Add(remove);

    var args = CommandLineStringSplitter.Instance.Split(normalized).ToArray();
    var parse = root.Parse(args);
    if (parse.Errors.Count > 0)
        return false;

    parse.Invoke();
    return true;
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
    table.AddRow("[yellow]/provider ...[/]", "Alias of /model commands");
    table.AddRow("[yellow]/undo [N][/]", "Undo last N tool snapshots (default: 1)");
    table.AddRow("[yellow]/snapshot [label][/]", "Create a manual snapshot");
    table.AddRow("[yellow]/history[/]", "List recent snapshots");
    table.AddRow("[yellow]/config[/]", "Show current provider details");
    table.AddRow("[yellow]/config check[/]", "Validate config and show issues");
    table.AddRow("[yellow]/config fix[/]", "Apply safe auto-fixes for common config issues");
    table.AddRow("[yellow]/swarm[/]", "Swarm mode commands");
    table.AddRow("[yellow]/swarm status[/]", "View active swarm status");
    table.AddRow("[yellow]/swarm cancel[/]", "Cancel active swarm and cleanup");
    table.AddRow("[yellow]/debug[/]", "Toggle subconscious stream (or /debug on|off)");
    table.AddRow("[yellow]/memory[/]", "Memory status (or /memory rebuild|compact)");
    table.AddRow("[yellow]/prompt[/]", "Prompt template commands (status|init)");
    table.AddRow("[yellow]/hook[/]", "Hook commands (status|enable|disable)");
    table.AddRow("[yellow]/status[/]", "Show runtime status snapshot");
    table.AddRow("[yellow]/locks[/]", "Show active coordination locks");
    table.AddRow("[yellow]/locks release <id>[/]", "Release one lock owned by current agent");
    table.AddRow("[yellow]/locks force-release <id>[/]", "Force release lock (leader/admin flow)");
    table.AddRow("[yellow]/todo[/]", "Todo list commands (list|add|done|remove)");
    table.AddRow("[yellow]/ui scroll up|down[/]", "Scroll interaction stream");
    table.AddRow("[yellow]/ui tab next|prev|main|swarm|todo[/]", "Switch left pane tab");
    table.AddRow("[yellow]/ui view next|prev|stream|swarm|todo|review[/]", "Switch center pane view");
    table.AddRow("[yellow]/ui worker next|prev|clear|<id>[/]", "Switch focused swarm worker");
    table.AddRow("[yellow]/review[/]", "Review commands (status|diff|list|use|approve|reject|reset)");
    table.AddRow("[yellow]/review approve apply[/]", "Approve and run review apply hooks");
    table.AddRow("[yellow]/review reject discard [--yes][/]", "Reject and discard tracked working tree changes");
    table.AddRow("[yellow]/clear[/]", "Clear screen and redraw UI");
    table.AddRow("[yellow]/exit[/]", "Exit PuddingCode");
    AnsiConsole.Write(table);
    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine("[grey]One-shot mode:[/] [yellow]pudding provider list|use|add|remove[/]");
    AnsiConsole.MarkupLine("[grey]Example:[/] [yellow]pudding provider add --id openai --endpoint https://api.openai.com/v1 --key <KEY> --model gpt-4o --billing per_token --in-usd-per-m 5 --out-usd-per-m 15[/]");
    AnsiConsole.WriteLine();

    var keymap = new Table().Border(TableBorder.None).HideHeaders()
        .AddColumn("key").AddColumn("effect");
    keymap.AddRow("[green]Enter[/]", "Send input");
    keymap.AddRow("[green]Tab[/]", "Complete slash command");
    keymap.AddRow("[green]Esc[/]", "Exit command picker");
    keymap.AddRow("[green]Up / Down[/]", "Move command selection");
    keymap.AddRow("[green]Ctrl+P / Ctrl+N[/]", "Previous / next input history");
    keymap.AddRow("[green]Ctrl+K[/]", "Clear current input");
    keymap.AddRow("[green]Ctrl+L[/]", "Clear screen and redraw UI");
    keymap.AddRow("[green]Ctrl+J[/]", "Send input (same as Enter)");
    keymap.AddRow("[green]PgUp / PgDn[/]", "Scroll interaction stream up / down");
    keymap.AddRow("[green]Alt+[ / Alt+][/]", "Switch left pane tab prev / next");
    keymap.AddRow("[green]Alt+1 / Alt+2 / Alt+3[/]", "Switch tab main / swarm / todo");
    keymap.AddRow("[green]Alt+4[/]", "Switch center view");
    keymap.AddRow("[green]Alt+5 / Alt+6[/]", "Focus next / prev swarm worker");
    keymap.AddRow("[green]Alt+7[/]", "Clear swarm worker focus");
    keymap.AddRow("[green]Alt+8[/]", "Open review view");
    AnsiConsole.Write(keymap);
}

void CmdUi(string[] parts)
{
    if (parts.Length < 2)
    {
        AnsiConsole.MarkupLine("[grey]Usage: /ui scroll [up|down|top|bottom] | /ui tab [next|prev|main|swarm|todo] | /ui view [next|prev|stream|swarm|todo|review] | /ui worker [next|prev|clear|<id>][/]");
        uiNeedsRender = true;
        return;
    }

    if (parts[1].Equals("tab", StringComparison.OrdinalIgnoreCase))
    {
        if (parts.Length < 3)
        {
            AnsiConsole.MarkupLine("[grey]Usage: /ui tab [next|prev|main|swarm|todo][/]");
            uiNeedsRender = true;
            return;
        }

        var mode = parts[2].ToLowerInvariant();
        switch (mode)
        {
            case "next":
                uiLeftTabIndex = (uiLeftTabIndex + 1) % uiLeftTabs.Length;
                break;
            case "prev":
                uiLeftTabIndex = (uiLeftTabIndex - 1 + uiLeftTabs.Length) % uiLeftTabs.Length;
                break;
            default:
                var idx = Array.FindIndex(uiLeftTabs, t => t.Equals(mode, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0) uiLeftTabIndex = idx;
                break;
        }
        uiNeedsRender = true;
        return;
    }

    if (parts[1].Equals("view", StringComparison.OrdinalIgnoreCase))
    {
        if (parts.Length < 3)
        {
            AnsiConsole.MarkupLine("[grey]Usage: /ui view [next|prev|stream|swarm|todo|review][/]");
            uiNeedsRender = true;
            return;
        }

        var mode = parts[2].ToLowerInvariant();
        switch (mode)
        {
            case "next":
                uiCenterViewIndex = (uiCenterViewIndex + 1) % uiCenterViews.Length;
                break;
            case "prev":
                uiCenterViewIndex = (uiCenterViewIndex - 1 + uiCenterViews.Length) % uiCenterViews.Length;
                break;
            default:
                var idx = Array.FindIndex(uiCenterViews, t => t.Equals(mode, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0) uiCenterViewIndex = idx;
                break;
        }
        uiNeedsRender = true;
        return;
    }

    if (parts[1].Equals("worker", StringComparison.OrdinalIgnoreCase))
    {
        if (parts.Length < 3)
        {
            AnsiConsole.MarkupLine("[grey]Usage: /ui worker [next|prev|clear|<id>][/]");
            uiNeedsRender = true;
            return;
        }

        var workers = workerManager.GetActiveWorkers()
            .OrderBy(w => w.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        uiCenterViewIndex = Array.FindIndex(uiCenterViews, t => t.Equals("swarm", StringComparison.OrdinalIgnoreCase));
        if (uiCenterViewIndex < 0) uiCenterViewIndex = 1;

        if (workers.Count == 0)
        {
            uiSwarmFocusWorkerId = null;
            uiNeedsRender = true;
            return;
        }

        var target = parts[2].ToLowerInvariant();
        if (target == "clear")
        {
            uiSwarmFocusWorkerId = null;
            uiNeedsRender = true;
            return;
        }

        var current = workers.FindIndex(w => w.Id.Equals(uiSwarmFocusWorkerId, StringComparison.OrdinalIgnoreCase));
        if (current < 0) current = 0;

        if (target == "next")
        {
            uiSwarmFocusWorkerId = workers[(current + 1) % workers.Count].Id;
            uiNeedsRender = true;
            return;
        }
        if (target == "prev")
        {
            uiSwarmFocusWorkerId = workers[(current - 1 + workers.Count) % workers.Count].Id;
            uiNeedsRender = true;
            return;
        }

        var byId = workers.FirstOrDefault(w =>
            w.Id.Equals(parts[2], StringComparison.OrdinalIgnoreCase) ||
            w.Id.Contains(parts[2], StringComparison.OrdinalIgnoreCase));
        if (byId is not null)
            uiSwarmFocusWorkerId = byId.Id;
        uiNeedsRender = true;
        return;
    }

    if (!parts[1].Equals("scroll", StringComparison.OrdinalIgnoreCase) || parts.Length < 3)
    {
        AnsiConsole.MarkupLine("[grey]Usage: /ui scroll [up|down|top|bottom] | /ui tab [next|prev|main|swarm|todo] | /ui view [next|prev|stream|swarm|todo|review] | /ui worker [next|prev|clear|<id>][/]");
        uiNeedsRender = true;
        return;
    }

    var total = interactionStream.Count;
    var windowSize = GetInteractionWindowSize();
    var maxOffset = Math.Max(0, total - windowSize);
    var step = Math.Max(1, windowSize / 2);
    var direction = parts[2].ToLowerInvariant();

    interactionScrollOffset = direction switch
    {
        "up" => Math.Min(maxOffset, interactionScrollOffset + step),
        "down" => Math.Max(0, interactionScrollOffset - step),
        "top" => maxOffset,
        "bottom" => 0,
        _ => interactionScrollOffset
    };
    uiNeedsRender = true;
}

async Task CmdReview(string[] parts)
{
    var sub = parts.Length > 1 ? parts[1].ToLowerInvariant() : "status";
    switch (sub)
    {
        case "status":
            if (reviewSelectedId is null)
            {
                var latest = reviewStore.GetLatest();
                if (latest is not null)
                    ApplyReviewItem(latest, select: true);
            }
            AnsiConsole.MarkupLine($"[grey]Review decision:[/] {reviewDecision.EscapeMarkup()}");
            AnsiConsole.MarkupLine($"[grey]Review summary :[/] {reviewSummary.EscapeMarkup()}");
            AnsiConsole.MarkupLine($"[grey]Updated at     :[/] {(reviewUpdatedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "-")}");
            AnsiConsole.MarkupLine($"[grey]Selected item  :[/] {(reviewSelectedId?.ToString() ?? "-")}");
            AnsiConsole.MarkupLine($"[grey]Queue size     :[/] {reviewStore.List().Count}");
            break;
        case "diff":
            var preview = await BuildReviewPreviewAsync();
            if (!preview.ok)
                break;
            var created = reviewStore.AddPending(preview.summary, preview.changedFiles, preview.lines);
            ApplyReviewItem(created, select: true);
            uiCenterViewIndex = Array.FindIndex(uiCenterViews, v => v.Equals("review", StringComparison.OrdinalIgnoreCase));
            if (uiCenterViewIndex < 0) uiCenterViewIndex = 0;
            AddInteraction($"[REVIEW] queued #{created.Id} ({preview.changedFiles} files)");
            break;
        case "list":
            ReviewList();
            break;
        case "use":
            if (parts.Length < 3 || !int.TryParse(parts[2], out var useId))
            {
                AnsiConsole.MarkupLine("[grey]Usage: /review use <id>[/]");
                break;
            }
            var item = reviewStore.Get(useId);
            if (item is null)
            {
                AnsiConsole.MarkupLine($"[red]Review item not found:[/] {useId}");
                break;
            }
            ApplyReviewItem(item, select: true);
            uiCenterViewIndex = Array.FindIndex(uiCenterViews, v => v.Equals("review", StringComparison.OrdinalIgnoreCase));
            if (uiCenterViewIndex < 0) uiCenterViewIndex = 0;
            AddInteraction($"[REVIEW] selected #{item.Id}");
            break;
        case "approve":
            {
                var selected = EnsureSelectedReviewItem();
                if (selected is null)
                {
                    AnsiConsole.MarkupLine("[grey]No review item selected. Run /review diff first.[/]");
                    break;
                }
                var apply = parts.Length > 2 && parts[2].Equals("apply", StringComparison.OrdinalIgnoreCase);
                string? approvedHash = null;
                var note = "Approved by operator.";
                if (snapshot.IsGitRepo)
                {
                    approvedHash = await snapshot.CreateSnapshotAsync($"review approved #{selected.Id}");
                    if (!string.IsNullOrWhiteSpace(approvedHash))
                        note = $"Approved and snapshotted: {approvedHash}";
                    else
                        note = "Approved (no file changes to snapshot).";
                }
                if (apply)
                {
                    var applyResult = await RunReviewApplyHooksAsync();
                    note += applyResult.ok
                        ? $" | apply hooks OK ({applyResult.total} command(s))"
                        : $" | apply hooks FAILED ({applyResult.failed}/{applyResult.total})";
                    if (!string.IsNullOrWhiteSpace(applyResult.summary))
                        note += $" | {applyResult.summary}";
                }
                var updated = reviewStore.SetDecision(
                    selected.Id,
                    "approved",
                    note: note,
                    approvedSnapshotHash: approvedHash);
                if (updated is not null)
                {
                    ApplyReviewItem(updated, select: true);
                    AddInteraction(apply
                        ? $"[REVIEW] approved+apply #{updated.Id}"
                        : $"[REVIEW] approved #{updated.Id}");
                }
            }
            break;
        case "reject":
            {
                var selected = EnsureSelectedReviewItem();
                if (selected is null)
                {
                    AnsiConsole.MarkupLine("[grey]No review item selected. Run /review diff first.[/]");
                    break;
                }
                var discard = parts.Length > 2 && parts[2].Equals("discard", StringComparison.OrdinalIgnoreCase);
                if (discard)
                {
                    var trackedPreview = await GetTrackedChangedFilesAsync();
                    if (trackedPreview.Count == 0)
                    {
                        AnsiConsole.MarkupLine("[grey]No tracked changes to discard. Rejecting as non-destructive.[/]");
                        discard = false;
                    }
                    else
                    {
                        var previewTable = new Table()
                            .Border(TableBorder.Rounded)
                            .AddColumn("Status")
                            .AddColumn("Path");
                        foreach (var (code, path) in trackedPreview.Take(20))
                            previewTable.AddRow(code.EscapeMarkup(), Clip(path, 88).EscapeMarkup());
                        AnsiConsole.MarkupLine("[yellow]Tracked changes that will be discarded:[/]");
                        AnsiConsole.Write(previewTable);
                        if (trackedPreview.Count > 20)
                            AnsiConsole.MarkupLine($"[grey]... and {trackedPreview.Count - 20} more file(s)[/]");
                    }

                    var forceYes = parts.Length > 3 && parts[3].Equals("--yes", StringComparison.OrdinalIgnoreCase);
                    if (discard && !forceYes)
                    {
                        var confirm = AnsiConsole.Confirm(
                            "[red]Discard tracked working tree changes?[/] [grey](this cannot be undone by /review)[/]",
                            false);
                        if (!confirm)
                        {
                            AnsiConsole.MarkupLine("[grey]Discard canceled.[/]");
                            break;
                        }
                    }

                    if (discard)
                    {
                        var discardOk = await DiscardTrackedChangesAsync();
                        if (!discardOk)
                        {
                            AnsiConsole.MarkupLine("[red]Failed to discard working tree changes. Review state kept pending.[/]");
                            break;
                        }
                    }
                }

                var rejectNote = discard
                    ? "Rejected and discarded tracked working tree changes."
                    : "Rejected by operator (non-destructive; repository changes kept).";
                var updated = reviewStore.SetDecision(
                    selected.Id,
                    "rejected",
                    note: rejectNote);
                if (updated is not null)
                {
                    ApplyReviewItem(updated, select: true);
                    AddInteraction(discard
                        ? $"[REVIEW] rejected+discarded #{updated.Id}"
                        : $"[REVIEW] rejected #{updated.Id}");
                }
            }
            break;
        case "reset":
            reviewDecision = "pending";
            reviewUpdatedAt = DateTimeOffset.Now;
            reviewSummary = "Review reset.";
            reviewPreviewLines = new List<string> { "Run /review diff to generate review preview." };
            reviewSelectedId = null;
            AddInteraction("[REVIEW] reset");
            break;
        default:
            AnsiConsole.MarkupLine("[grey]Usage: /review [status|diff|list|use <id>|approve [apply]|reject [discard [--yes]]|reset][/]");
            break;
    }
}

void ReviewList()
{
    var items = reviewStore.List().Take(10).ToList();
    if (items.Count == 0)
    {
        AnsiConsole.MarkupLine("[grey]No review items. Run /review diff to create one.[/]");
        return;
    }

    var table = new Table()
        .Border(TableBorder.Rounded)
        .AddColumn("ID")
        .AddColumn("Decision")
        .AddColumn("Changed")
        .AddColumn("Summary")
        .AddColumn("Created");

    foreach (var item in items)
    {
        var decision = item.Decision switch
        {
            "approved" => "[green]approved[/]",
            "rejected" => "[red]rejected[/]",
            _ => "[yellow]pending[/]"
        };
        table.AddRow(
            item.Id.ToString(),
            decision,
            item.ChangedFiles.ToString(),
            Clip(item.Summary, 34).EscapeMarkup(),
            item.CreatedAt.LocalDateTime.ToString("MM-dd HH:mm"));
    }

    AnsiConsole.Write(table);
}

ReviewQueueItem? EnsureSelectedReviewItem()
{
    if (reviewSelectedId is not null)
    {
        var byId = reviewStore.Get(reviewSelectedId.Value);
        if (byId is not null) return byId;
    }
    return reviewStore.GetLatest();
}

void ApplyReviewItem(ReviewQueueItem item, bool select)
{
    if (select)
        reviewSelectedId = item.Id;
    reviewDecision = item.Decision;
    reviewSummary = item.Summary;
    reviewPreviewLines = item.PreviewLines.Count == 0
        ? new List<string> { "No diff changes." }
        : item.PreviewLines;
    reviewUpdatedAt = item.ReviewedAt ?? item.CreatedAt;
}

async Task<(bool ok, int changedFiles, string summary, List<string> lines)> BuildReviewPreviewAsync()
{
    if (!snapshot.IsGitRepo)
    {
        reviewDecision = "pending";
        reviewSummary = "No git repo.";
        reviewPreviewLines = new List<string> { "Initialize git first (git init)." };
        reviewUpdatedAt = DateTimeOffset.Now;
        return (false, 0, reviewSummary, reviewPreviewLines);
    }

    var status = await RunGitAsync("status", "--short");
    var diffUnstaged = await RunGitAsync("diff", "--no-color");
    var diffStaged = await RunGitAsync("diff", "--cached", "--no-color");
    if (status.code != 0 || diffUnstaged.code != 0 || diffStaged.code != 0)
    {
        reviewDecision = "pending";
        reviewSummary = "Failed to load git diff.";
        reviewPreviewLines = new List<string>
        {
            Clip(status.error, 120),
            Clip(diffUnstaged.error, 120),
            Clip(diffStaged.error, 120)
        }.Where(x => !string.IsNullOrWhiteSpace(x) && x != "-").ToList();
        if (reviewPreviewLines.Count == 0)
            reviewPreviewLines.Add("Unknown git error.");
        reviewUpdatedAt = DateTimeOffset.Now;
        return (false, 0, reviewSummary, reviewPreviewLines);
    }

    var changedCount = status.output
        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Length;
    var blocks = new List<string>();
    if (!string.IsNullOrWhiteSpace(diffStaged.output))
    {
        blocks.Add("## staged");
        blocks.AddRange(diffStaged.output.Split('\n'));
    }
    if (!string.IsNullOrWhiteSpace(diffUnstaged.output))
    {
        blocks.Add("## unstaged");
        blocks.AddRange(diffUnstaged.output.Split('\n'));
    }
    if (blocks.Count == 0)
        blocks.Add("No diff changes.");

    reviewDecision = "pending";
    reviewSummary = $"Changed files: {changedCount}";
    reviewPreviewLines = blocks
        .Select(l => l.TrimEnd('\r'))
        .Take(120)
        .ToList();
    reviewUpdatedAt = DateTimeOffset.Now;
    return (true, changedCount, reviewSummary, reviewPreviewLines);
}

async Task<(int code, string output, string error)> RunGitAsync(params string[] args)
{
    var psi = new ProcessStartInfo
    {
        FileName = "git",
        WorkingDirectory = project.RootPath,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };
    foreach (var arg in args)
        psi.ArgumentList.Add(arg);

    using var process = Process.Start(psi);
    if (process is null)
        return (-1, "", "failed to start process");

    var output = await process.StandardOutput.ReadToEndAsync();
    var error = await process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    return (process.ExitCode, output, error);
}

async Task<bool> DiscardTrackedChangesAsync()
{
    if (!snapshot.IsGitRepo)
        return false;

    // Reset tracked files to HEAD, keep untracked files intact.
    var r1 = await RunGitAsync("reset", "--hard", "HEAD");
    if (r1.code != 0)
        return false;

    reviewSummary = "Working tree reset to HEAD after reject discard.";
    reviewPreviewLines = new List<string> { "Tracked changes discarded via git reset --hard HEAD." };
    reviewUpdatedAt = DateTimeOffset.Now;
    return true;
}

async Task<(bool ok, int total, int failed, string summary)> RunReviewApplyHooksAsync()
{
    var commands = LoadReviewApplyCommands();
    if (commands.Count == 0)
        return (true, 0, 0, "No hooks configured (.pudding/review/hooks.txt)");

    var failures = 0;
    var summaries = new List<string>();
    foreach (var cmd in commands)
    {
        var (code, output, error) = await RunShellCommandAsync(cmd);
        if (code != 0)
            failures++;

        var brief = code == 0 ? "ok" : $"fail:{code}";
        var detail = !string.IsNullOrWhiteSpace(error) ? Clip(error, 80) : Clip(output, 80);
        summaries.Add($"{brief} {Clip(cmd, 30)} {detail}");
    }

    return (failures == 0, commands.Count, failures, string.Join(" | ", summaries.Take(2)));
}

List<string> LoadReviewApplyCommands()
{
    var path = Path.Combine(project.RootPath, ".pudding", "review", "hooks.txt");
    if (!File.Exists(path))
        return [];

    return File.ReadAllLines(path)
        .Select(x => x.Trim())
        .Where(x => !string.IsNullOrWhiteSpace(x) && !x.StartsWith('#'))
        .Take(8)
        .ToList();
}

async Task<(int code, string output, string error)> RunShellCommandAsync(string command)
{
    ProcessStartInfo psi;
    if (OperatingSystem.IsWindows())
    {
        psi = new ProcessStartInfo
        {
            FileName = "cmd",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = project.RootPath
        };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add(command);
    }
    else
    {
        psi = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = project.RootPath
        };
        psi.ArgumentList.Add("-lc");
        psi.ArgumentList.Add(command);
    }

    using var process = Process.Start(psi);
    if (process is null)
        return (-1, "", "failed to start shell process");
    var output = await process.StandardOutput.ReadToEndAsync();
    var error = await process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    return (process.ExitCode, output, error);
}

async Task<List<(string status, string path)>> GetTrackedChangedFilesAsync()
{
    var result = await RunGitAsync("status", "--short");
    if (result.code != 0 || string.IsNullOrWhiteSpace(result.output))
        return [];

    var rows = new List<(string status, string path)>();
    foreach (var raw in result.output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
    {
        var line = raw.TrimEnd('\r');
        if (line.Length < 3)
            continue;
        var code = line[..2];
        var path = line[3..].Trim();
        if (code == "??" || code == "!!")
            continue;
        rows.Add((code, path));
    }
    return rows;
}

SwarmSessionState? LoadLatestSwarmSessionState()
{
    var path = Path.Combine(project.RootPath, ".pudding", "swarm", "runtime", "latest-session.json");
    if (!File.Exists(path))
        return null;
    try
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<SwarmSessionState>(json);
    }
    catch
    {
        return null;
    }
}

static string TaskStatusMarkup(SwarmTaskStatus status) => status switch
{
    SwarmTaskStatus.Completed => "[green]done[/]",
    SwarmTaskStatus.InProgress => "[blue]run[/]",
    SwarmTaskStatus.Assigned => "[yellow]asg[/]",
    SwarmTaskStatus.PendingReview => "[yellow]rev[/]",
    SwarmTaskStatus.Testing => "[yellow]tst[/]",
    SwarmTaskStatus.Blocked => "[red]blk[/]",
    SwarmTaskStatus.Failed => "[red]fail[/]",
    SwarmTaskStatus.Abandoned => "[grey]abd[/]",
    _ => "[grey]new[/]"
};

void CmdPrompt(string[] parts)
{
    var sub = parts.Length > 1 ? parts[1].ToLowerInvariant() : "status";
    var promptDir = Path.Combine(project.RootPath, ".pudding", "prompts");

    switch (sub)
    {
        case "status":
            PromptStatus(promptDir);
            break;
        case "init":
            PromptInit(promptDir);
            break;
        default:
            AnsiConsole.MarkupLine("[grey]Usage: /prompt [status | init][/]");
            break;
    }
}

void CmdHook(string[] parts)
{
    var sub = parts.Length > 1 ? parts[1].ToLowerInvariant() : "status";
    switch (sub)
    {
        case "status":
            HookStatus();
            break;
        case "enable" when parts.Length > 2:
            HookEnable(parts[2]);
            break;
        case "disable" when parts.Length > 2:
            HookDisable(parts[2]);
            break;
        default:
            AnsiConsole.MarkupLine("[grey]Usage: /hook [status | enable <metrics|audit_file|external> | disable <metrics|audit_file|external>][/]");
            break;
    }
}

void HookStatus()
{
    var table = new Table()
        .Border(TableBorder.Rounded)
        .AddColumn("Hook")
        .AddColumn("Enabled")
        .AddColumn("Detail");

    var enabled = new HashSet<string>(hookRuntime.EnabledHooks, StringComparer.OrdinalIgnoreCase);
    table.AddRow(
        "metrics",
        enabled.Contains("metrics") ? "[green]yes[/]" : "[grey]no[/]",
        $"pre/post tool={hookMetrics.PreToolCalls}/{hookMetrics.PostToolCalls}, pre/post reply={hookMetrics.PreReplies}/{hookMetrics.PostReplies}");
    table.AddRow(
        "audit_file",
        enabled.Contains("audit_file") ? "[green]yes[/]" : "[grey]no[/]",
        (hookRuntime.AuditFilePath ?? "-").EscapeMarkup());
    table.AddRow(
        "external",
        enabled.Contains("external") ? "[green]yes[/]" : "[grey]no[/]",
        hookRuntime.ExternalHookNames.Count == 0
            ? "-"
            : string.Join(", ", hookRuntime.ExternalHookNames).EscapeMarkup());

    AnsiConsole.Write(table);
}

void HookEnable(string name)
{
    var key = name.Trim().ToLowerInvariant();
    if (key is not ("metrics" or "audit_file" or "external"))
    {
        AnsiConsole.MarkupLine("[red]Unknown hook. Supported: metrics, audit_file, external[/]");
        return;
    }

    if (!config.Hooks.Enabled.Any(h => h.Equals(key, StringComparison.OrdinalIgnoreCase)))
        config.Hooks.Enabled.Add(key);

    ConfigManager.Save(configPath, config);
    hookRuntime = HookRegistry.Build(project.RootPath, config.Hooks);
    hookMetrics = hookRuntime.Metrics;
    SwitchProvider(active);
    AnsiConsole.MarkupLine($"[green]Hook enabled:[/] {key}");
}

void HookDisable(string name)
{
    var key = name.Trim().ToLowerInvariant();
    if (key is not ("metrics" or "audit_file" or "external"))
    {
        AnsiConsole.MarkupLine("[red]Unknown hook. Supported: metrics, audit_file, external[/]");
        return;
    }

    config.Hooks.Enabled.RemoveAll(h => h.Equals(key, StringComparison.OrdinalIgnoreCase));
    if (config.Hooks.Enabled.Count == 0)
        config.Hooks.Enabled.Add("metrics");

    ConfigManager.Save(configPath, config);
    hookRuntime = HookRegistry.Build(project.RootPath, config.Hooks);
    hookMetrics = hookRuntime.Metrics;
    SwitchProvider(active);
    AnsiConsole.MarkupLine($"[green]Hook disabled:[/] {key}");
}

void PromptStatus(string promptDir)
{
    var files = new[] { "system.md", "spirit.md", "leader.md", "worker.md" };
    var table = new Table()
        .Border(TableBorder.Rounded)
        .AddColumn("File")
        .AddColumn("State")
        .AddColumn("Path");

    foreach (var name in files)
    {
        var path = Path.Combine(promptDir, name);
        var exists = File.Exists(path);
        table.AddRow(
            name.EscapeMarkup(),
            exists ? "[green]exists[/]" : "[yellow]missing[/]",
            path.EscapeMarkup());
    }

    AnsiConsole.Write(table);
}

void PromptInit(string promptDir)
{
    Directory.CreateDirectory(promptDir);

    var templates = new Dictionary<string, string>
    {
        ["system.md"] = """
            You are PuddingCode running in role: {{role}}.
            Project: {{project_name}}
            Root: {{project_root}}
            Use tools when needed and keep outputs concise.
            """,
        ["spirit.md"] = """
            You are the default coding assistant.
            Prioritize correctness, clear steps, and safe file changes.
            """,
        ["leader.md"] = """
            You are the swarm leader.
            Split tasks, define contracts, and verify worker outputs.
            """,
        ["worker.md"] = """
            You are a scoped worker.
            Scope:
            {{worker_scope}}
            Never modify files outside scope.
            """,
        ["explore.md"] = """
            You are the explorer.
            Map project structure, architecture patterns, conventions, and risk hotspots.
            Prefer read-only tools and produce concise findings.
            """,
        ["researcher.md"] = """
            You are the researcher.
            Investigate best practices, compare options, and cite trade-offs clearly.
            """,
        ["planner.md"] = """
            You are the planner.
            Ask clarifying questions, then produce a phased plan with acceptance criteria and guardrails.
            """,
        ["reviewer.md"] = """
            You are the reviewer.
            Review changed code for bugs, regressions, missing tests, and policy violations.
            Findings first, ordered by severity.
            """
    };

    var created = 0;
    foreach (var kv in templates)
    {
        var path = Path.Combine(promptDir, kv.Key);
        if (File.Exists(path))
            continue;

        File.WriteAllText(path, kv.Value + Environment.NewLine, Encoding.UTF8);
        created++;
    }

    AnsiConsole.MarkupLine($"[green]Prompt templates ready.[/] created={created}, dir={promptDir.EscapeMarkup()}");
}

void CmdClear()
{
    AnsiConsole.Clear();
    uiNeedsRender = true;
}

void CmdConfig()
{
    AnsiConsole.MarkupLine($"[grey]Config file  : {configPath.EscapeMarkup()}[/]");
    AnsiConsole.MarkupLine($"[grey]Provider     : {active.Id.EscapeMarkup()} - {active.Name.EscapeMarkup()}[/]");
    AnsiConsole.MarkupLine($"[grey]Endpoint     : {active.Endpoint.EscapeMarkup()}[/]");
    AnsiConsole.MarkupLine($"[grey]Model        : {active.Model.EscapeMarkup()}[/]");
    AnsiConsole.MarkupLine($"[grey]Billing      : {active.Billing.Mode}[/]");
    if (active.Billing.Mode == BillingMode.PerToken)
    {
        AnsiConsole.MarkupLine($"[grey]Input $/1M    : {active.Billing.InputUsdPerMillionTokens}[/]");
        AnsiConsole.MarkupLine($"[grey]Output $/1M   : {active.Billing.OutputUsdPerMillionTokens}[/]");
    }
    else if (active.Billing.Mode == BillingMode.PerRequest)
    {
        AnsiConsole.MarkupLine($"[grey]$/request     : {active.Billing.RequestUsd}[/]");
        AnsiConsole.MarkupLine($"[grey]Req quota/mo  : {active.Billing.IncludedRequestsPerMonth}[/]");
    }
    else if (active.Billing.Mode == BillingMode.PerSession)
    {
        AnsiConsole.MarkupLine($"[grey]$/session     : {active.Billing.SessionUsd}[/]");
        AnsiConsole.MarkupLine($"[grey]Sess quota/mo : {active.Billing.IncludedSessionsPerMonth}[/]");
    }
    else if (active.Billing.Mode == BillingMode.MonthlyFlat)
    {
        AnsiConsole.MarkupLine($"[grey]$/month       : {active.Billing.MonthlyUsd}[/]");
    }
    AnsiConsole.MarkupLine($"[grey]Conscious    : {dualModelConfig.Conscious.ProviderId.EscapeMarkup()} / {dualModelConfig.Conscious.Model.EscapeMarkup()}[/]");
    AnsiConsole.MarkupLine($"[grey]Subconscious : {dualModelConfig.Subconscious.ProviderId.EscapeMarkup()} / {dualModelConfig.Subconscious.Model.EscapeMarkup()}[/]");
    AnsiConsole.MarkupLine($"[grey]Sub-stream   : {(subconsciousVisible ? "ON" : "OFF")}[/]");
    AnsiConsole.MarkupLine($"[grey]Hooks enabled: {string.Join(", ", hookRuntime.EnabledHooks).EscapeMarkup()}[/]");
    if (!string.IsNullOrWhiteSpace(hookRuntime.AuditFilePath))
        AnsiConsole.MarkupLine($"[grey]Hook audit log: {hookRuntime.AuditFilePath!.EscapeMarkup()}[/]");
    AnsiConsole.MarkupLine($"[grey]Context max tokens : {contextBudget.MaxPromptTokens}[/]");
    AnsiConsole.MarkupLine($"[grey]Context max history: {contextBudget.MaxHistoryMessages}[/]");
    AnsiConsole.MarkupLine($"[grey]Context preserve   : {contextBudget.PreserveTailMessages}[/]");
    if (active.Temperature.HasValue)
        AnsiConsole.MarkupLine($"[grey]Temperature  : {active.Temperature.Value}[/]");
    if (active.MaxTokens.HasValue)
        AnsiConsole.MarkupLine($"[grey]Max Tokens   : {active.MaxTokens.Value}[/]");
    AnsiConsole.MarkupLine($"[grey]Providers    : {config.Providers.Count} total[/]");
    AnsiConsole.MarkupLine($"[grey]Project      : {project.RootPath.EscapeMarkup()}[/]");
}

void CmdTodo(string[] parts)
{
    var sub = parts.Length > 1 ? parts[1].ToLowerInvariant() : "list";
    switch (sub)
    {
        case "list":
            TodoList();
            break;
        case "add":
            TodoAdd(parts);
            break;
        case "done":
            TodoDone(parts);
            break;
        case "remove":
        case "rm":
            TodoRemove(parts);
            break;
        default:
            AnsiConsole.MarkupLine("[grey]Usage: /todo [list | add <text> | done <id> | remove <id>][/]");
            break;
    }
}

void TodoList()
{
    var items = todoStore.List();
    if (items.Count == 0)
    {
        AnsiConsole.MarkupLine("[grey]Todo is empty.[/]");
        return;
    }

    var table = new Table()
        .Border(TableBorder.Rounded)
        .AddColumn("ID")
        .AddColumn("State")
        .AddColumn("Title")
        .AddColumn("Time");

    foreach (var item in items)
    {
        table.AddRow(
            item.Id.ToString(),
            item.Done ? "[green]done[/]" : "[yellow]open[/]",
            item.Title.EscapeMarkup(),
            item.CreatedAt.LocalDateTime.ToString("MM-dd HH:mm"));
    }

    AnsiConsole.Write(table);
}

void TodoAdd(string[] parts)
{
    if (parts.Length < 3)
    {
        AnsiConsole.MarkupLine("[grey]Usage: /todo add <text>[/]");
        return;
    }

    var title = string.Join(' ', parts.Skip(2)).Trim();
    if (string.IsNullOrWhiteSpace(title))
    {
        AnsiConsole.MarkupLine("[red]Todo title cannot be empty.[/]");
        return;
    }

    var item = todoStore.Add(title);
    AnsiConsole.MarkupLine($"[green]Added todo #[/][yellow]{item.Id}[/] {item.Title.EscapeMarkup()}");
}

void TodoDone(string[] parts)
{
    if (parts.Length < 3 || !int.TryParse(parts[2], out var id))
    {
        AnsiConsole.MarkupLine("[grey]Usage: /todo done <id>[/]");
        return;
    }

    var updated = todoStore.MarkDone(id);
    if (updated is null)
    {
        if (!uiPinnedLayout)
            AnsiConsole.MarkupLine($"[red]Todo #{id} not found.[/]");
        return;
    }

    AnsiConsole.MarkupLine($"[green]Marked done:[/] #{updated.Id} {updated.Title.EscapeMarkup()}");
}

void TodoRemove(string[] parts)
{
    if (parts.Length < 3 || !int.TryParse(parts[2], out var id))
    {
        AnsiConsole.MarkupLine("[grey]Usage: /todo remove <id>[/]");
        return;
    }

    if (!todoStore.Remove(id))
    {
        if (!uiPinnedLayout)
            AnsiConsole.MarkupLine($"[red]Todo #{id} not found.[/]");
        return;
    }

    AnsiConsole.MarkupLine($"[green]Removed todo #[/][yellow]{id}[/]");
}

void CmdStatus()
{
    var uptime = DateTimeOffset.Now - sessionStartedAt;
    var estInput = sessionInputTokens;
    var estOutput = sessionOutputTokens;
    var estTotal = sessionInputTokens + sessionOutputTokens;
    var cost = BillingEstimator.FormatCost(
        active.Billing,
        sessionInputTokens,
        sessionOutputTokens,
        sessionLlmRequests,
        sessionTurns);
    var workerCount = workerManager.GetActiveWorkers().Count;
    var lockCount = centralLockManager.ListActiveLocksAsync().GetAwaiter().GetResult().Count;

    AnsiConsole.MarkupLine("[grey]Runtime status[/]");
    AnsiConsole.MarkupLine($"[grey]Uptime          : {uptime:hh\\:mm\\:ss}[/]");
    AnsiConsole.MarkupLine($"[grey]Turns           : {sessionTurns}[/]");
    AnsiConsole.MarkupLine($"[grey]Token est input : {estInput}[/]");
    AnsiConsole.MarkupLine($"[grey]Token est output: {estOutput}[/]");
    AnsiConsole.MarkupLine($"[grey]Token est total : {estTotal}[/]");
    AnsiConsole.MarkupLine($"[grey]LLM requests    : {sessionLlmRequests}[/]");
    AnsiConsole.MarkupLine($"[grey]Billing mode    : {active.Billing.Mode}[/]");
    AnsiConsole.MarkupLine($"[grey]Cost estimate   : {cost}[/]");
    AnsiConsole.MarkupLine($"[grey]Hooks pre tool  : {hookMetrics.PreToolCalls}[/]");
    AnsiConsole.MarkupLine($"[grey]Hooks post tool : {hookMetrics.PostToolCalls}[/]");
    AnsiConsole.MarkupLine($"[grey]Hooks pre reply : {hookMetrics.PreReplies}[/]");
    AnsiConsole.MarkupLine($"[grey]Hooks post reply: {hookMetrics.PostReplies}[/]");
    AnsiConsole.MarkupLine($"[grey]Hook errors     : {hookMetrics.HookErrors}[/]");
    AnsiConsole.MarkupLine($"[grey]Tool calls      : {sessionToolCalls}[/]");
    AnsiConsole.MarkupLine($"[grey]Tool results    : {sessionToolResults}[/]");
    AnsiConsole.MarkupLine($"[grey]Errors          : {sessionErrors}[/]");
    AnsiConsole.MarkupLine($"[grey]Active workers  : {workerCount}[/]");
    AnsiConsole.MarkupLine($"[grey]Active locks    : {lockCount}[/]");
}

void CmdLocks(string[] parts)
{
    if (parts.Length >= 3 && parts[1].Equals("release", StringComparison.OrdinalIgnoreCase))
    {
        var ok = centralLockManager.ReleaseAsync(parts[2], "spirit", force: false).GetAwaiter().GetResult();
        AnsiConsole.MarkupLine(ok
            ? $"[green]Released lock[/] [yellow]{parts[2].EscapeMarkup()}[/]"
            : $"[red]Cannot release lock[/] [yellow]{parts[2].EscapeMarkup()}[/]");
        return;
    }

    if (parts.Length >= 3 && parts[1].Equals("force-release", StringComparison.OrdinalIgnoreCase))
    {
        var ok = centralLockManager.ReleaseAsync(parts[2], "leader", force: true).GetAwaiter().GetResult();
        AnsiConsole.MarkupLine(ok
            ? $"[green]Force released lock[/] [yellow]{parts[2].EscapeMarkup()}[/]"
            : $"[red]Cannot force release lock[/] [yellow]{parts[2].EscapeMarkup()}[/]");
        return;
    }

    var locks = centralLockManager.ListActiveLocksAsync().GetAwaiter().GetResult();
    if (locks.Count == 0)
    {
        AnsiConsole.MarkupLine("[grey]No active locks.[/]");
        return;
    }

    var table = new Table()
        .Border(TableBorder.Rounded)
        .AddColumn("[bold]LockId[/]")
        .AddColumn("[bold]Owner[/]")
        .AddColumn("[bold]Type[/]")
        .AddColumn("[bold]Targets[/]")
        .AddColumn("[bold]Expire[/]");

    foreach (var l in locks)
    {
        var targets = string.Join(", ", l.Targets.Take(2).Select(t => Path.GetFileName(t.Path)));
        table.AddRow(
            l.Id.EscapeMarkup(),
            $"{l.OwnerAgentName.EscapeMarkup()} ({l.OwnerAgentId.EscapeMarkup()})",
            l.Type.ToString(),
            targets.EscapeMarkup(),
            l.ExpireAt.ToLocalTime().ToString("HH:mm:ss"));
    }

    AnsiConsole.Write(table);
}

void CmdOpen(string[] parts)
{
    var path = parts.Length > 1
        ? string.Join(' ', parts.Skip(1))
        : Environment.CurrentDirectory;

    if (!Directory.Exists(path))
    {
        if (!uiPinnedLayout)
            AnsiConsole.MarkupLine($"[red]Directory not found: {path.EscapeMarkup()}[/]");
        return;
    }

    project = new ProjectContext(path);
    guard = new PermissionGuard(project.RootPath);
    centralLockManager = new CentralLockManager(project.RootPath);
    registry = new ToolRegistry();
    registry.Register(new FileTool(project, guard, centralLockManager));
    registry.Register(new ShellTool(project, guard));
    snapshot = new GitSnapshotService(project.RootPath);
    subconscious = CreateSubconsciousEngine(project.RootPath);
    todoStore = new TodoListStore(project.RootPath);
    reviewStore = new ReviewQueueStore(project.RootPath);
    hookRuntime = HookRegistry.Build(project.RootPath, config.Hooks);
    hookMetrics = hookRuntime.Metrics;
    contextBudget = BuildContextBudget();
    agent = new AgentOrchestrator(
        gateway,
        registry,
        project,
        snapshot,
        hooks: hookRuntime.Hooks,
        contextBudget: contextBudget);
    reviewSelectedId = null;
    reviewDecision = "pending";
    reviewSummary = "No review snapshot.";
    reviewPreviewLines = new List<string> { "Run /review diff to generate review preview." };
    reviewUpdatedAt = null;
    var latest = reviewStore.GetLatest();
    if (latest is not null)
        ApplyReviewItem(latest, select: true);

    AnsiConsole.MarkupLine($"[green]鉁揫/] Project opened: [yellow]{project.RootPath.EscapeMarkup()}[/]");
    AnsiConsole.MarkupLine("[grey]Conversation history has been reset.[/]");
    if (snapshot.IsGitRepo)
        AnsiConsole.MarkupLine("[grey]Git repo detected - auto-snapshots enabled[/]");
}

ContextBudgetOptions BuildContextBudget()
{
    var cb = config.ContextBudget;
    return new ContextBudgetOptions(
        MaxPromptTokens: cb.MaxPromptTokens,
        MaxHistoryMessages: cb.MaxHistoryMessages,
        PreserveTailMessages: cb.PreserveTailMessages);
}

void RunFirstLaunchOnboarding()
{
    AnsiConsole.MarkupLine("[yellow]Welcome! First launch onboarding[/]");
    AnsiConsole.MarkupLine($"[grey]Config -> {configPath.EscapeMarkup()}[/]");
    AnsiConsole.MarkupLine("[grey]Choose an initialization path:[/]");
    var choice = AnsiConsole.Prompt(
        new SelectionPrompt<string>()
            .Title("[green]Initialization mode[/]:")
            .AddChoices(
                "Local Ollama (quick start)",
                "Cloud provider (interactive)",
                "Generate YAML scaffold"));

    switch (choice)
    {
        case "Local Ollama (quick start)":
            AddDefaultOllamaProvider();
            break;
        case "Generate YAML scaffold":
            InitYamlScaffold();
            break;
        default:
            AddCloudProviderInteractive();
            break;
    }
}

void AddDefaultOllamaProvider()
{
    var model = AnsiConsole.Prompt(
        new TextPrompt<string>("[green]Ollama model[/] [grey](default: llama3.1)[/]:")
            .DefaultValue("llama3.1"));
    var endpoint = AnsiConsole.Prompt(
        new TextPrompt<string>("[green]Ollama endpoint[/] [grey](default: http://localhost:11434/v1)[/]:")
            .DefaultValue("http://localhost:11434/v1"));

    var p = new ProviderEntry
    {
        Id = "ollama/" + model,
        Name = "Ollama",
        Endpoint = endpoint,
        ApiKey = "ollama",
        Model = model,
        Billing = new ProviderBillingConfig { Mode = BillingMode.LocalFree }
    };

    if (ProviderConfigService.TryAdd(config, p, out var error))
    {
        config.ActiveProvider = p.Id;
        ConfigManager.Save(configPath, config);
        AnsiConsole.MarkupLine($"[green]Initialized local provider:[/] {p.Id.EscapeMarkup()}");
    }
    else
    {
        AnsiConsole.MarkupLine($"[yellow]{error.EscapeMarkup()}[/]");
    }
}

void AddCloudProviderInteractive()
{
    var first = PromptNewProvider();
    if (!ProviderConfigService.TryAdd(config, first, out var error))
    {
        AnsiConsole.MarkupLine($"[red]{error.EscapeMarkup()}[/]");
        return;
    }

    config.ActiveProvider = first.Id;
    ConfigManager.Save(configPath, config);
    AnsiConsole.MarkupLine($"\n[green]Provider [yellow]{first.Id.EscapeMarkup()}[/] saved and activated.[/]");

    while (AnsiConsole.Confirm("[grey]Add another provider?[/]", false))
    {
        var extra = PromptNewProvider();
        if (!ProviderConfigService.TryAdd(config, extra, out var e2))
        {
            AnsiConsole.MarkupLine($"[red]{e2.EscapeMarkup()}[/]");
            continue;
        }
        ConfigManager.Save(configPath, config);
        AnsiConsole.MarkupLine($"[green]Provider [yellow]{extra.Id.EscapeMarkup()}[/] saved.[/]");
    }
}

void InitYamlScaffold()
{
    var root = Environment.CurrentDirectory;
    var providersDir = Path.Combine(root, "providers");
    Directory.CreateDirectory(providersDir);
    var globalPath = Path.Combine(root, "pudding.yaml");
    var providerPath = Path.Combine(providersDir, "ollama.yaml");

    if (!File.Exists(globalPath))
    {
        File.WriteAllText(globalPath, "providers_dir: providers\nactive_provider: ollama/llama3.1\n");
    }

    if (!File.Exists(providerPath))
    {
        File.WriteAllText(providerPath,
            "id: ollama\nname: Ollama\nendpoint: http://localhost:11434/v1\napi_key: ollama\nmodels:\n  - id: llama3.1\nbilling:\n  mode: local_free\n");
    }

    AnsiConsole.MarkupLine($"[green]YAML scaffold created:[/] {globalPath.EscapeMarkup()}");
    AnsiConsole.MarkupLine($"[green]Provider file created:[/] {providerPath.EscapeMarkup()}");
}

void ShowConfigHealth(bool forceShowOk = false)
{
    var issues = ConfigDiagnostics.Validate(config, usingEnv);
    if (issues.Count == 0 && !forceShowOk)
        return;

    if (issues.Count == 0)
    {
        AnsiConsole.MarkupLine("[green]Config health: OK[/]");
        return;
    }

    var table = new Table()
        .Border(TableBorder.Rounded)
        .AddColumn("Level")
        .AddColumn("Issue")
        .AddColumn("Hint");

    foreach (var issue in issues.Take(12))
    {
        var level = issue.Level.Equals("error", StringComparison.OrdinalIgnoreCase)
            ? "[red]error[/]"
            : "[yellow]warn[/]";
        table.AddRow(
            level,
            issue.Message.EscapeMarkup(),
            (issue.FixHint ?? "-").EscapeMarkup());
    }

    AnsiConsole.Write(table);
    var fixable = issues.Count(i => i.Fixable);
    AnsiConsole.MarkupLine($"[grey]Tip: run /config check after editing configs. Fixable issues: {fixable}[/]");
}

void CmdConfigFix()
{
    if (usingEnv)
    {
        AnsiConsole.MarkupLine("[grey]Environment mode is active. No auto-fix needed for file config.[/]");
        return;
    }

    if (config.WorkspaceYaml is { Enabled: true })
    {
        AnsiConsole.MarkupLine("[yellow]Workspace YAML mode enabled.[/]");
        AnsiConsole.MarkupLine("[grey]Please fix YAML source files directly: pudding.yaml and providers/*.yaml[/]");
        ShowConfigHealth(forceShowOk: true);
        return;
    }

    var changes = 0;
    var notes = new List<string>();

    if (config.Providers.Count == 0)
    {
        InitYamlScaffold();
        config = ConfigManager.Load(configPath, workspaceRoot);
        notes.Add("Generated YAML scaffold.");
        changes++;
    }

    foreach (var p in config.Providers)
    {
        if (string.IsNullOrWhiteSpace(p.Model))
        {
            var idx = p.Id.IndexOf('/');
            if (idx >= 0 && idx < p.Id.Length - 1)
            {
                p.Model = p.Id[(idx + 1)..];
                notes.Add($"Inferred model for {p.Id}");
                changes++;
            }
        }

        if (string.IsNullOrWhiteSpace(p.Endpoint) &&
            p.Id.StartsWith("ollama", StringComparison.OrdinalIgnoreCase))
        {
            p.Endpoint = "http://localhost:11434/v1";
            notes.Add($"Applied default endpoint for {p.Id}");
            changes++;
        }

        if (string.IsNullOrWhiteSpace(p.ApiKey) && IsLocalEndpoint(p.Endpoint))
        {
            p.ApiKey = "ollama";
            if (p.Billing.Mode == BillingMode.PerToken)
                p.Billing.Mode = BillingMode.LocalFree;
            notes.Add($"Applied local api key for {p.Id}");
            changes++;
        }
    }

    if (config.Providers.Count > 0 &&
        (string.IsNullOrWhiteSpace(config.ActiveProvider) ||
         !config.Providers.Any(p => p.Id.Equals(config.ActiveProvider, StringComparison.OrdinalIgnoreCase))))
    {
        config.ActiveProvider = config.Providers[0].Id;
        notes.Add($"Set active provider -> {config.ActiveProvider}");
        changes++;
    }

    if (changes == 0)
    {
        AnsiConsole.MarkupLine("[green]No auto-fix needed. Configuration looks healthy.[/]");
        ShowConfigHealth(forceShowOk: true);
        return;
    }

    ConfigManager.Save(configPath, config);
    config = ConfigManager.Load(configPath, workspaceRoot);
    if (config.Providers.Count > 0)
    {
        var target = config.Providers.Find(p => p.Id == config.ActiveProvider) ?? config.Providers[0];
        SwitchProvider(target);
    }

    var panel = new Panel(string.Join(Environment.NewLine, notes.Select(n => "- " + n)))
        .Header("[bold green]Config Auto-Fix Applied[/]")
        .Border(BoxBorder.Rounded)
        .BorderColor(Color.Green);
    AnsiConsole.Write(panel);
    ShowConfigHealth(forceShowOk: true);
}

bool IsLocalEndpoint(string endpoint)
{
    if (string.IsNullOrWhiteSpace(endpoint))
        return false;
    var text = endpoint.ToLowerInvariant();
    return text.Contains("localhost")
           || text.Contains("127.0.0.1")
           || text.Contains("0.0.0.0")
           || text.Contains("ollama");
}

void CmdDebug(string[] parts)
{
    if (parts.Length > 1)
    {
        var mode = parts[1].ToLowerInvariant();
        subconsciousVisible = mode switch
        {
            "on" => true,
            "off" => false,
            _ => subconsciousVisible
        };
    }
    else
    {
        subconsciousVisible = !subconsciousVisible;
    }

    var state = subconsciousVisible ? "ON" : "OFF";
    AnsiConsole.MarkupLine($"[grey]Subconscious stream: [yellow]{state}[/][/]");
}

async Task CmdMemory(string[] parts)
{
    var sub = parts.Length > 1 ? parts[1].ToLowerInvariant() : "status";
    switch (sub)
    {
        case "status":
            var status = await subconscious.GetMemoryStatusAsync();
            AnsiConsole.MarkupLine("[grey]Memory status[/]");
            AnsiConsole.MarkupLine($"[grey]Project entries : {status.ProjectEntries}[/]");
            AnsiConsole.MarkupLine($"[grey]Global entries  : {status.GlobalEntries}[/]");
            AnsiConsole.MarkupLine($"[grey]Indexed entries : {status.IndexedEntries}[/]");
            AnsiConsole.MarkupLine($"[grey]Writes since compact : {status.WritesSinceCompact}/{status.CompactWriteThreshold}[/]");
            AnsiConsole.MarkupLine($"[grey]Compact policy      : min={status.CompactMinEntries}, keep={status.CompactKeepEntries}[/]");
            AnsiConsole.MarkupLine($"[grey]Recall hit (recent) : {status.RecallHitsInWindow}/{status.RecallWindowSize} ({status.RecentRecallHitRate:P1})[/]");
            AnsiConsole.MarkupLine($"[grey]Compaction gain     : last +{status.LastCompactionSavedEntries} ({status.LastCompactionGainRate:P1}), total +{status.TotalCompactionSavedEntries}[/]");
            AnsiConsole.MarkupLine($"[grey]Compaction runs     : {status.CompactionRuns}[/]");
            AnsiConsole.MarkupLine($"[grey]Model summary       : {(status.UseModelSummarization ? "ON" : "OFF")}[/]");
            AnsiConsole.MarkupLine($"[grey]Model summary budget: {status.ModelSummaryTokensUsedToday}/{status.ModelSummaryDailyTokenBudget} tokens/day[/]");
            AnsiConsole.MarkupLine($"[grey]Model summary calls : {status.ModelSummaryCalls}[/]");
            AnsiConsole.MarkupLine($"[grey]Last compacted  : {(status.LastCompactedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "never")}[/]");
            break;
        case "rebuild":
            await subconscious.RebuildMemoryIndexAsync();
            AnsiConsole.MarkupLine("[green]鉁揫/] Memory index rebuilt.");
            break;
        case "compact":
            await subconscious.RunMaintenanceAsync();
            AnsiConsole.MarkupLine("[green]鉁揫/] Memory maintenance completed.");
            break;
        default:
            AnsiConsole.MarkupLine("[grey]Usage: /memory [status | rebuild | compact][/]");
            break;
    }
}

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
        AnsiConsole.MarkupLine("[grey]Nothing to undo - no pudding snapshots found.[/]");
    else
        AnsiConsole.MarkupLine($"[green]鉁揫/] Undid [yellow]{undone}[/] snapshot(s). Changes are back in your working tree.");
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
        AnsiConsole.MarkupLine("[grey]Nothing to snapshot - no changes detected.[/]");
    else
        AnsiConsole.MarkupLine($"[green]鉁揫/] Snapshot [yellow]{hash}[/]: {label.EscapeMarkup()}");
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
        var marker = p.Id == active.Id ? "[green]鈼廩/]" : "[grey]鈼媅/]";
        table.AddRow(marker, p.Id.EscapeMarkup(), p.Model.EscapeMarkup(), p.Endpoint.EscapeMarkup());
    }

    AnsiConsole.Write(table);
}

void ModelAdd()
{
    var p = PromptNewProvider();
    if (!ProviderConfigService.TryAdd(config, p, out var error))
    {
        AnsiConsole.MarkupLine($"[red]{error.EscapeMarkup()}[/]");
        return;
    }

    ConfigManager.Save(configPath, config);
    AnsiConsole.MarkupLine($"[green]鉁揫/] Provider [yellow]{p.Id.EscapeMarkup()}[/] added.");
    AnsiConsole.MarkupLine($"[grey]Switch with: /model use {p.Id.EscapeMarkup()}[/]");
}

void ModelUse(string targetId)
{
    if (!ProviderConfigService.TrySetActive(config, targetId, out var target, out var error))
    {
        if (!uiPinnedLayout)
            AnsiConsole.MarkupLine($"[red]{error.EscapeMarkup()} Use /model to list.[/]");
        return;
    }

    ConfigManager.Save(configPath, config);
    SwitchProvider(target!);
    AnsiConsole.MarkupLine($"[green]鉁揫/] Switched to [yellow]{target!.Id.EscapeMarkup()}[/] ({target.Model.EscapeMarkup()})");
    AnsiConsole.MarkupLine("[grey]Conversation history has been reset.[/]");
}

void ModelRemove(string targetId)
{
    if (!ProviderConfigService.TryRemove(config, targetId, out var removed, out var switchedTo, out var error))
    {
        if (!uiPinnedLayout)
            AnsiConsole.MarkupLine($"[red]{error.EscapeMarkup()}[/]");
        return;
    }

    if (!string.IsNullOrWhiteSpace(switchedTo))
    {
        var newActive = config.Providers.First(p => p.Id.Equals(switchedTo, StringComparison.OrdinalIgnoreCase));
        SwitchProvider(newActive);
        AnsiConsole.MarkupLine($"[grey]Active provider switched to \"{active.Id.EscapeMarkup()}\"[/]");
    }

    ConfigManager.Save(configPath, config);
    AnsiConsole.MarkupLine($"[green]鉁揫/] Provider \"{removed!.Id.EscapeMarkup()}\" removed.");
}

ProviderEntry PromptNewProvider()
{
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

    var epPrompt = new TextPrompt<string>(
        "[green]API Base URL[/] [grey](/chat/completions will be appended automatically)[/]:");
    if (!string.IsNullOrEmpty(defEndpoint))
        epPrompt.DefaultValue(defEndpoint);
    var ep = AnsiConsole.Prompt(epPrompt);

    var key = AnsiConsole.Prompt(
        new TextPrompt<string>("[green]API Key[/]:").Secret());

    var modelPrompt = new TextPrompt<string>("[green]Model name[/]:");
    if (!string.IsNullOrEmpty(defModel))
        modelPrompt.DefaultValue(defModel);
    var mdl = AnsiConsole.Prompt(modelPrompt);

    var tempStr = AnsiConsole.Prompt(
        new TextPrompt<string>("[green]Temperature[/] [grey](0.0-2.0, Enter to skip = model default)[/]:")
            .AllowEmpty());
    double? temp = double.TryParse(tempStr, out var tv) ? tv : null;

    var maxStr = AnsiConsole.Prompt(
        new TextPrompt<string>("[green]Max Output Tokens[/] [grey](e.g. 4096/8192, Enter to skip)[/]:")
            .AllowEmpty());
    int? maxTok = int.TryParse(maxStr, out var mv) ? mv : null;

    var billingMode = AnsiConsole.Prompt(
        new SelectionPrompt<BillingMode>()
            .Title("[green]Billing mode[/]:")
            .AddChoices(
                BillingMode.PerToken,
                BillingMode.PerRequest,
                BillingMode.PerSession,
                BillingMode.MonthlyFlat,
                BillingMode.LocalFree));

    var billing = new ProviderBillingConfig
    {
        Mode = billingMode
    };

    if (billingMode == BillingMode.PerToken)
    {
        var inPrice = AnsiConsole.Prompt(
            new TextPrompt<string>("[green]Input USD / 1M tokens[/] [grey](Enter to skip)[/]:")
                .AllowEmpty());
        var outPrice = AnsiConsole.Prompt(
            new TextPrompt<string>("[green]Output USD / 1M tokens[/] [grey](Enter to skip)[/]:")
                .AllowEmpty());
        if (decimal.TryParse(inPrice, out var iv))
            billing.InputUsdPerMillionTokens = iv;
        if (decimal.TryParse(outPrice, out var ov))
            billing.OutputUsdPerMillionTokens = ov;
    }
    else if (billingMode == BillingMode.PerRequest)
    {
        var reqPrice = AnsiConsole.Prompt(
            new TextPrompt<string>("[green]USD per request[/] [grey](Enter to skip)[/]:")
                .AllowEmpty());
        var quota = AnsiConsole.Prompt(
            new TextPrompt<string>("[green]Included requests / month[/] [grey](Enter to skip)[/]:")
                .AllowEmpty());
        if (decimal.TryParse(reqPrice, out var rv))
            billing.RequestUsd = rv;
        if (int.TryParse(quota, out var rq))
            billing.IncludedRequestsPerMonth = rq;
    }
    else if (billingMode == BillingMode.PerSession)
    {
        var sessPrice = AnsiConsole.Prompt(
            new TextPrompt<string>("[green]USD per session[/] [grey](Enter to skip)[/]:")
                .AllowEmpty());
        var quota = AnsiConsole.Prompt(
            new TextPrompt<string>("[green]Included sessions / month[/] [grey](Enter to skip)[/]:")
                .AllowEmpty());
        if (decimal.TryParse(sessPrice, out var sv))
            billing.SessionUsd = sv;
        if (int.TryParse(quota, out var sq))
            billing.IncludedSessionsPerMonth = sq;
    }
    else if (billingMode == BillingMode.MonthlyFlat)
    {
        var monthly = AnsiConsole.Prompt(
            new TextPrompt<string>("[green]Monthly USD[/] [grey](Enter to skip)[/]:")
                .AllowEmpty());
        if (decimal.TryParse(monthly, out var mv2))
            billing.MonthlyUsd = mv2;
    }

    var name = template == "Custom endpoint" ? id : template;

    return new ProviderEntry
    {
        Id = id,
        Name = name,
        Endpoint = ep,
        ApiKey = key,
        Model = mdl,
        Temperature = temp,
        MaxTokens = maxTok,
        Billing = billing
    };
}




