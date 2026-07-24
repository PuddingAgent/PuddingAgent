using Microsoft.Extensions.Logging;
using PuddingCodeIntelligence.Contracts;
using PuddingCodeIntelligence.CSharp;
using PuddingCodeIntelligence.Storage;

// ── Logging setup ──────────────────────────────────────────────────────────
using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddConsole();
    builder.SetMinimumLevel(LogLevel.Information);
});

var logger = loggerFactory.CreateLogger<RoslynCSharpIndexer>();

// ── Constants ──────────────────────────────────────────────────────────────
const string WorkspaceId = "cli";
string dbPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "PuddingCodeIndexer", "code-index.db");

// ── Command dispatch ───────────────────────────────────────────────────────
if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

string command = args[0].ToLowerInvariant();

switch (command)
{
    case "index":
        return await RunIndexAsync(args);
    case "search":
        return await RunSearchAsync(args);
    case "status":
        return await RunStatusAsync();
    default:
        Console.WriteLine($"Unknown command: {command}");
        PrintUsage();
        return 1;
}

// ── index <project-path> ───────────────────────────────────────────────────
async Task<int> RunIndexAsync(string[] args)
{
    if (args.Length < 2)
    {
        Console.WriteLine("Usage: PuddingCodeIndexer.Cli index <project-path>");
        return 1;
    }

    string projectPath = Path.GetFullPath(args[1]);
    if (!Directory.Exists(projectPath))
    {
        Console.WriteLine($"Error: directory does not exist: {projectPath}");
        return 1;
    }

    string projectId = new DirectoryInfo(projectPath).Name;

    Console.WriteLine($"Database : {dbPath}");
    Console.WriteLine($"Project  : {projectPath}");
    Console.WriteLine($"ProjectId: {projectId}");
    Console.WriteLine();

    var store = new SqliteCodeIndexStore(dbPath);
    await store.InitializeAsync();

    // Discover .csproj files under the project path
    var csprojFiles = Directory
        .GetFiles(projectPath, "*.csproj", SearchOption.AllDirectories)
        .Where(p => !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                 && !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
        .ToList();

    // Discover .sln / .slnx files
    string? solutionPath = Directory
        .GetFiles(projectPath, "*.sln*", SearchOption.TopDirectoryOnly)
        .FirstOrDefault();

    var descriptor = new CodeWorkspaceDescriptor(
        WorkspaceId: WorkspaceId,
        ProjectId: projectId,
        ProjectPath: projectPath,
        IsLoaded: true,
        SolutionPath: solutionPath,
        ProjectFilePaths: csprojFiles);

    Console.WriteLine($"Solution : {solutionPath ?? "(none)"}");
    Console.WriteLine($"CSProj   : {csprojFiles.Count} file(s)");
    Console.WriteLine();
    Console.WriteLine("Indexing...");

    var indexer = new RoslynCSharpIndexer(store, logger);
    CodeIndexResult result = await indexer.IndexWorkspaceAsync(descriptor);

    Console.WriteLine();
    Console.WriteLine($"Success  : {result.Success}");
    Console.WriteLine($"Status   : {result.Status}");
    if (!string.IsNullOrEmpty(result.Message))
        Console.WriteLine($"Message  : {result.Message}");

    return result.Success ? 0 : 1;
}

// ── search <query> ─────────────────────────────────────────────────────────
async Task<int> RunSearchAsync(string[] args)
{
    if (args.Length < 2)
    {
        Console.WriteLine("Usage: PuddingCodeIndexer.Cli search <query>");
        return 1;
    }

    string query = string.Join(" ", args.Skip(1));

    var store = new SqliteCodeIndexStore(dbPath);
    await store.InitializeAsync();

    var request = new CodeSymbolSearchRequest(
        WorkspaceId: WorkspaceId,
        Query: query,
        Limit: 30);

    IReadOnlyList<CodeSymbolRecord> symbols = await store.SearchSymbolsAsync(request);

    if (symbols.Count == 0)
    {
        Console.WriteLine($"No symbols found for: {query}");
        return 0;
    }

    Console.WriteLine($"Found {symbols.Count} symbol(s) for: {query}");
    Console.WriteLine();

    foreach (var sym in symbols)
    {
        Console.WriteLine($"  [{sym.Kind}] {sym.Name}");
        if (!string.IsNullOrEmpty(sym.Signature))
            Console.WriteLine($"         {sym.Signature}");
        Console.WriteLine($"         {sym.FilePath}:{sym.StartLine}-{sym.EndLine}");
        Console.WriteLine();
    }

    return 0;
}

// ── status ─────────────────────────────────────────────────────────────────
async Task<int> RunStatusAsync()
{
    var store = new SqliteCodeIndexStore(dbPath);
    await store.InitializeAsync();

    IReadOnlyList<CodeProjectRecord> projects = await store.ListProjectsAsync(WorkspaceId);

    if (projects.Count == 0)
    {
        Console.WriteLine("No indexed projects found.");
        Console.WriteLine($"Database: {dbPath}");
        return 0;
    }

    Console.WriteLine($"Database: {dbPath}");
    Console.WriteLine($"Projects ({projects.Count}):");
    Console.WriteLine();

    foreach (var proj in projects)
    {
        Console.WriteLine($"  {proj.ProjectId}");
        Console.WriteLine($"    Path   : {proj.ProjectPath}");
        Console.WriteLine($"    Status : {proj.Status}");
        if (!string.IsNullOrEmpty(proj.DisplayName))
            Console.WriteLine($"    Name   : {proj.DisplayName}");
        if (proj.UpdatedAtUtc.HasValue)
            Console.WriteLine($"    Updated: {proj.UpdatedAtUtc.Value:u}");
        Console.WriteLine();
    }

    return 0;
}

// ── Usage ──────────────────────────────────────────────────────────────────
void PrintUsage()
{
    Console.WriteLine("PuddingCodeIndexer.Cli — standalone code indexing tool");
    Console.WriteLine();
    Console.WriteLine("Commands:");
    Console.WriteLine("  index <project-path>   Index a project directory");
    Console.WriteLine("  search <query>         Search indexed symbols");
    Console.WriteLine("  status                 Show indexed project status");
}
