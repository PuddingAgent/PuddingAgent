using Microsoft.Extensions.Logging;
using PuddingCodeIntelligence.Contracts;
using PuddingCodeIntelligence.CSharp;
using PuddingCodeIntelligence.Storage;
using PuddingCodeIntelligence.TypeScript;

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

    // ── TypeScript/JavaScript indexing ─────────────────────────────────────
    var tsExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".ts", ".tsx", ".js", ".jsx" };
    var tsFiles = Directory
        .GetFiles(projectPath, "*.*", SearchOption.AllDirectories)
        .Where(f => tsExtensions.Contains(Path.GetExtension(f))
                 && !f.Contains(Path.DirectorySeparatorChar + "node_modules" + Path.DirectorySeparatorChar)
                 && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                 && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
        .ToList();

    if (tsFiles.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine($"TypeScript/JavaScript files found: {tsFiles.Count}");
        Console.WriteLine("Indexing TypeScript/JavaScript...");

        // Locate the extraction script and node_modules.
        // AppContext.BaseDirectory is bin/Debug/net10.0/ — walk up to find Scripts/ with node_modules.
        string cliScriptsDir = Path.Combine(AppContext.BaseDirectory, "Scripts");
        string extractScript = Path.Combine(cliScriptsDir, "extract-ts-symbols.js");
        string cliNodeModules = Path.Combine(cliScriptsDir, "node_modules");

        // If node_modules not in output dir, walk up to find source Scripts/ directory
        if (!Directory.Exists(cliNodeModules))
        {
            string? walkDir = new DirectoryInfo(AppContext.BaseDirectory).Parent?.FullName;
            for (int i = 0; i < 5 && walkDir is not null; i++)
            {
                string candidate = Path.Combine(walkDir, "Scripts", "node_modules");
                if (Directory.Exists(candidate))
                {
                    cliScriptsDir = Path.Combine(walkDir, "Scripts");
                    extractScript = Path.Combine(cliScriptsDir, "extract-ts-symbols.js");
                    cliNodeModules = candidate;
                    break;
                }
                walkDir = new DirectoryInfo(walkDir).Parent?.FullName;
            }
        }

        // TypeScriptIndexer expects script at ProjectPath/Scripts/extract-ts-symbols.js
        string targetScriptsDir = Path.Combine(projectPath, "Scripts");
        string targetScriptPath = Path.Combine(targetScriptsDir, "extract-ts-symbols.js");

        bool copiedScript = false;
        if (!File.Exists(targetScriptPath) && File.Exists(extractScript))
        {
            Directory.CreateDirectory(targetScriptsDir);
            File.Copy(extractScript, targetScriptPath, overwrite: true);
            copiedScript = true;
        }

        // Set NODE_PATH so the extraction script can find the 'typescript' module
        if (Directory.Exists(cliNodeModules))
        {
            string? existingNodePath = Environment.GetEnvironmentVariable("NODE_PATH");
            string newNodePath = string.IsNullOrEmpty(existingNodePath)
                ? cliNodeModules
                : $"{existingNodePath}{Path.PathSeparator}{cliNodeModules}";
            Environment.SetEnvironmentVariable("NODE_PATH", newNodePath);
        }

        string tsProjectId = projectId + "_ts";
        var tsDescriptor = new CodeWorkspaceDescriptor(
            WorkspaceId: WorkspaceId,
            ProjectId: tsProjectId,
            ProjectPath: projectPath,
            IsLoaded: true,
            SolutionPath: null,
            ProjectFilePaths: new List<string>());

        var tsLogger = loggerFactory.CreateLogger<TypeScriptIndexer>();
        var tsIndexer = new TypeScriptIndexer(store, tsLogger);
        CodeIndexResult tsResult = await tsIndexer.IndexWorkspaceAsync(tsDescriptor);

        Console.WriteLine();
        Console.WriteLine($"TS Success  : {tsResult.Success}");
        Console.WriteLine($"TS Status   : {tsResult.Status}");
        if (!string.IsNullOrEmpty(tsResult.Message))
            Console.WriteLine($"TS Message  : {tsResult.Message}");

        // Clean up copied script if we copied it
        if (copiedScript)
        {
            try { File.Delete(targetScriptPath); } catch { /* best effort */ }
            try { Directory.Delete(targetScriptsDir, recursive: false); } catch { /* dir may not be empty */ }
        }

        if (!tsResult.Success)
            Console.WriteLine("Warning: TypeScript indexing failed. C# index is still valid.");
    }
    else
    {
        Console.WriteLine();
        Console.WriteLine("No TypeScript/JavaScript files found — skipping TS indexing.");
    }

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
