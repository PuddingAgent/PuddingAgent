using Microsoft.Extensions.Logging;
using PuddingCodeIntelligence.Contracts;
using PuddingCodeIntelligence.CSharp;
using PuddingCodeIntelligence.Storage;
using PuddingCodeIntelligence.Python;
using PuddingCodeIntelligence.TypeScript;
using PuddingCodeIntelligence.Services;

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
    case "watch":
        return await RunWatchAsync(args);
    case "definition":
        return await RunDefinitionAsync(args);
    case "references":
        return await RunReferencesAsync(args);
    case "hover":
        return await RunHoverAsync(args);
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

    // ── Python indexing ─────────────────────────────────────────────────────
    var pyExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".py" };
    var pyExcludedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "__pycache__", ".git", "venv", ".venv" };
    var pyFiles = Directory
        .GetFiles(projectPath, "*.py", SearchOption.AllDirectories)
        .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(seg => pyExcludedDirs.Contains(seg))
                 && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                 && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
        .ToList();

    if (pyFiles.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine($"Python files found: {pyFiles.Count}");
        Console.WriteLine("Indexing Python...");

        // Locate the extraction script
        string pyCliScriptsDir = Path.Combine(AppContext.BaseDirectory, "Scripts");
        string pyExtractScript = Path.Combine(pyCliScriptsDir, "extract-py-symbols.py");

        // If not in output dir, walk up to find source Scripts/ directory
        if (!File.Exists(pyExtractScript))
        {
            string? walkDir = new DirectoryInfo(AppContext.BaseDirectory).Parent?.FullName;
            for (int i = 0; i < 5 && walkDir is not null; i++)
            {
                string candidate = Path.Combine(walkDir, "Scripts", "extract-py-symbols.py");
                if (File.Exists(candidate))
                {
                    pyCliScriptsDir = Path.Combine(walkDir, "Scripts");
                    pyExtractScript = candidate;
                    break;
                }
                walkDir = new DirectoryInfo(walkDir).Parent?.FullName;
            }
        }

        // PythonIndexer expects script at ProjectPath/Scripts/extract-py-symbols.py
        string pyTargetScriptsDir = Path.Combine(projectPath, "Scripts");
        string pyTargetScriptPath = Path.Combine(pyTargetScriptsDir, "extract-py-symbols.py");

        bool copiedPyScript = false;
        if (!File.Exists(pyTargetScriptPath) && File.Exists(pyExtractScript))
        {
            Directory.CreateDirectory(pyTargetScriptsDir);
            File.Copy(pyExtractScript, pyTargetScriptPath, overwrite: true);
            copiedPyScript = true;
        }

        string pyProjectId = projectId + "_py";
        var pyDescriptor = new CodeWorkspaceDescriptor(
            WorkspaceId: WorkspaceId,
            ProjectId: pyProjectId,
            ProjectPath: projectPath,
            IsLoaded: true,
            SolutionPath: null,
            ProjectFilePaths: new List<string>());

        var pyLogger = loggerFactory.CreateLogger<PythonIndexer>();
        var pyIndexer = new PythonIndexer(store, pyLogger);
        CodeIndexResult pyResult = await pyIndexer.IndexWorkspaceAsync(pyDescriptor);

        Console.WriteLine();
        Console.WriteLine($"PY Success  : {pyResult.Success}");
        Console.WriteLine($"PY Status   : {pyResult.Status}");
        if (!string.IsNullOrEmpty(pyResult.Message))
            Console.WriteLine($"PY Message  : {pyResult.Message}");

        // Clean up copied script if we copied it
        if (copiedPyScript)
        {
            try { File.Delete(pyTargetScriptPath); } catch { /* best effort */ }
            try { Directory.Delete(pyTargetScriptsDir, recursive: false); } catch { /* dir may not be empty */ }
        }

        if (!pyResult.Success)
            Console.WriteLine("Warning: Python indexing failed. C# index is still valid.");
    }
    else
    {
        Console.WriteLine();
        Console.WriteLine("No Python files found — skipping Python indexing.");
    }

    return result.Success ? 0 : 1;
}

// ── watch <project-path> ───────────────────────────────────────────────────
async Task<int> RunWatchAsync(string[] args)
{
    if (args.Length < 2)
    {
        Console.WriteLine("Usage: PuddingCodeIndexer.Cli watch <project-path>");
        return 1;
    }

    string projectPath = Path.GetFullPath(args[1]);
    if (!Directory.Exists(projectPath))
    {
        Console.WriteLine($"Error: directory does not exist: {projectPath}");
        return 1;
    }

    // Run initial full index
    Console.WriteLine("=== Initial full index ===");
    var indexArgs = new string[] { "index", args[1] };
    await RunIndexAsync(indexArgs);

    // Set up file watching
    var watchExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".cs", ".ts", ".tsx", ".js", ".jsx", ".py" };
    var excludedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "bin", "obj", "node_modules", ".git", "__pycache__", ".pudding-code" };

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
        Console.WriteLine("\nStopping watcher...");
    };

    Console.WriteLine();
    Console.WriteLine($"Watching for changes in: {projectPath}");
    Console.WriteLine("Press Ctrl+C to stop.");
    Console.WriteLine();

    // Debounce state
    CancellationTokenSource? pendingDebounce = null;
    var debounceLock = new object();

    using var watcher = new FileSystemWatcher(projectPath)
    {
        IncludeSubdirectories = true,
        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime
    };

    watcher.Filters.Add("*.*");

    void OnChanged(object sender, FileSystemEventArgs e)
    {
        string ext = Path.GetExtension(e.FullPath);
        if (!watchExtensions.Contains(ext))
            return;

        // Check excluded directories
        string relativePath = Path.GetRelativePath(projectPath, e.FullPath);
        var segments = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (segments.Any(seg => excludedDirs.Contains(seg)))
            return;

        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Changed: {e.FullPath}");

        lock (debounceLock)
        {
            pendingDebounce?.Cancel();
            pendingDebounce = new CancellationTokenSource();
            var token = pendingDebounce.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), token);
                    if (token.IsCancellationRequested) return;

                    Console.WriteLine();
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Re-indexing...");
                    await RunIndexAsync(indexArgs);
                    Console.WriteLine();
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Watching for changes...");
                }
                catch (OperationCanceledException)
                {
                    // Debounced — another change came in
                }
            });
        }
    }

    watcher.Changed += OnChanged;
    watcher.Created += OnChanged;
    watcher.Renamed += (s, e) => OnChanged(s, e);
    watcher.Deleted += OnChanged;

    watcher.EnableRaisingEvents = true;

    try
    {
        await Task.Delay(Timeout.Infinite, cts.Token);
    }
    catch (OperationCanceledException)
    {
        // Ctrl+C pressed
    }

    return 0;
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

// ── definition <symbol-name> ─────────────────────────────────────────────
async Task<int> RunDefinitionAsync(string[] args)
{
    if (args.Length < 2)
    {
        Console.WriteLine("Usage: PuddingCodeIndexer.Cli definition <symbol-name>");
        return 1;
    }

    string symbolName = string.Join(" ", args.Skip(1));

    var store = new SqliteCodeIndexStore(dbPath);
    await store.InitializeAsync();
    var queryService = new CodeQueryService(store);

    var request = new CodeSymbolSearchRequest(
        WorkspaceId: WorkspaceId,
        Query: symbolName,
        Limit: 30);

    IReadOnlyList<CodeSymbolDetail> symbols = await queryService.SearchSymbolsAsync(request);

    if (symbols.Count == 0)
    {
        Console.WriteLine($"No definition found for: {symbolName}");
        return 0;
    }

    Console.WriteLine($"Definition of '{symbolName}':");
    foreach (var detail in symbols)
    {
        var sym = detail.Symbol;
        Console.WriteLine($"  {sym.Kind} {sym.Name} :: {sym.FilePath}:{sym.StartLine}");
    }

    return 0;
}

// ── references <symbol-name> ─────────────────────────────────────────────
async Task<int> RunReferencesAsync(string[] args)
{
    if (args.Length < 2)
    {
        Console.WriteLine("Usage: PuddingCodeIndexer.Cli references <symbol-name>");
        return 1;
    }

    string symbolName = string.Join(" ", args.Skip(1));

    var store = new SqliteCodeIndexStore(dbPath);
    await store.InitializeAsync();
    var queryService = new CodeQueryService(store);

    // Search for the symbol to get its ID
    var request = new CodeSymbolSearchRequest(
        WorkspaceId: WorkspaceId,
        Query: symbolName,
        Limit: 10);

    IReadOnlyList<CodeSymbolDetail> symbols = await queryService.SearchSymbolsAsync(request);

    if (symbols.Count == 0)
    {
        Console.WriteLine($"No symbol found for: {symbolName}");
        return 0;
    }

    var targetSymbol = symbols[0].Symbol;

    // Get callers via CodeQueryService
    IReadOnlyList<CodeRelationRecord> callers = await queryService.GetCallersAsync(
        targetSymbol.WorkspaceId,
        targetSymbol.ProjectId,
        targetSymbol.SymbolId);

    // Build reference list: declaration + callers
    var references = new List<(string FilePath, int? Line, string Kind)>();
    references.Add((targetSymbol.FilePath, targetSymbol.StartLine, "declaration"));

    foreach (var rel in callers)
    {
        string filePath = rel.SourceFilePath ?? "(unknown)";
        int? line = rel.SourceLine;

        // Resolve source symbol if file path is missing
        if (rel.SourceFilePath is null)
        {
            var sourceSymbol = await store.GetSymbolAsync(
                rel.WorkspaceId, rel.ProjectId, rel.SourceSymbolId);
            if (sourceSymbol is not null)
            {
                filePath = sourceSymbol.FilePath;
                line = sourceSymbol.StartLine;
            }
        }

        references.Add((filePath, line, rel.Kind.ToString().ToLowerInvariant()));
    }

    Console.WriteLine($"References to '{symbolName}' ({references.Count} found):");
    foreach (var (filePath, line, kind) in references)
    {
        string lineStr = line.HasValue ? $":{line.Value}" : "";
        Console.WriteLine($"  {filePath}{lineStr}  {kind}");
    }

    return 0;
}

// ── hover <symbol-name> ──────────────────────────────────────────────────
async Task<int> RunHoverAsync(string[] args)
{
    if (args.Length < 2)
    {
        Console.WriteLine("Usage: PuddingCodeIndexer.Cli hover <symbol-name>");
        return 1;
    }

    string symbolName = string.Join(" ", args.Skip(1));

    var store = new SqliteCodeIndexStore(dbPath);
    await store.InitializeAsync();
    var queryService = new CodeQueryService(store);

    var request = new CodeSymbolSearchRequest(
        WorkspaceId: WorkspaceId,
        Query: symbolName,
        Limit: 10);

    IReadOnlyList<CodeSymbolDetail> symbols = await queryService.SearchSymbolsAsync(request);

    if (symbols.Count == 0)
    {
        Console.WriteLine($"No symbol found for: {symbolName}");
        return 0;
    }

    var detail = symbols[0];
    var sym = detail.Symbol;
    string signature = sym.Signature ?? sym.Name;

    Console.WriteLine($"Hover: {signature}");
    Console.WriteLine($"File: {sym.FilePath}:{sym.StartLine}");
    Console.WriteLine($"Kind: {sym.Kind}");

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
    Console.WriteLine("  watch <project-path>   Watch and re-index on file changes");
    Console.WriteLine("  definition <name>      Find symbol definition location");
    Console.WriteLine("  references <name>      Find all references to a symbol");
    Console.WriteLine("  hover <name>           Show symbol info and signature");
}
