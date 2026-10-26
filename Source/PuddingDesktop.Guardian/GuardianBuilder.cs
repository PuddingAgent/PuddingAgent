using System.Diagnostics;

namespace PuddingDesktop.Guardian;

public sealed class GuardianBuilder(GuardianSettings settings)
{
    public async Task<string> PrepareAsync(GuardianCommand command, CancellationToken ct)
    {
        if (command.DeploymentMode == "prebuilt-artifact")
        {
            var supplied = Path.GetFullPath(command.ArtifactDirectory!);
            ValidateCandidate(supplied, command.Target, command.ArtifactAssemblySha256);
            if (command.FrontendMode != "skip") throw new ArgumentException("Prebuilt artifacts must already contain their frontend.");
            return supplied;
        }
        var repo = settings.RepositoryRoot ?? throw new InvalidOperationException("Configure a source repository before building.");
        SafeFiles.NoLinks(repo);
        var project = Path.Combine(repo, "Source", command.Target == "core" ? "PuddingAgent/PuddingAgent.csproj" : "PuddingDesktop/PuddingDesktop.csproj");
        if (!File.Exists(project)) throw new FileNotFoundException("Product project not found.");
        var workspace = Path.Combine(repo, "temp", "build", "guardian", Guid.NewGuid().ToString("N"));
        var candidate = Path.Combine(workspace, "candidate");
        Directory.CreateDirectory(workspace);
        var frontend = Path.Combine(Path.GetDirectoryName(settings.CoreExecutablePath)!, "wwwroot", "admin");
        if (command.FrontendMode == "build")
        {
            frontend = Path.Combine(workspace, "frontend");
            await RunAsync("cmd.exe", ["/d", "/c", "pnpm.cmd", "run", "build"], Path.Combine(repo, "Source", "PuddingPlatformAdmin"),
                Path.Combine(workspace, "frontend.log"), new() { ["PUDDING_ADMIN_OUTPUT_PATH"] = frontend }, ct);
        }
        else if (command.FrontendMode == "load")
        {
            frontend = Path.GetFullPath(command.FrontendArtifactDirectory!);
            if (command.FrontendArtifactIndexSha256 is { Length: > 0 } hash
                && !SafeFiles.Hash(Path.Combine(frontend, "index.html")).Equals(hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Frontend hash mismatch.");
        }
        if (!File.Exists(Path.Combine(frontend, "index.html"))) throw new FileNotFoundException("Frontend index.html is required.");
        var arguments = new List<string> { "publish", project, "--artifacts-path", Path.Combine(workspace, "artifacts"), "-c", settings.Configuration,
            "-r", "win-x64", "--self-contained", "false", "-o", candidate, "--nologo", "-p:PuddingAdminDistPath=" + frontend,
            "-p:SkipCoreBundleBuild=true", "-p:SkipExitObserverBuild=true", "-p:SkipGuardianBundle=true" };
        await RunAsync("dotnet", arguments, repo, Path.Combine(workspace, "build.log"), null, ct);
        ValidateCandidate(candidate, command.Target, command.ArtifactAssemblySha256);
        return candidate;
    }

    public void ValidateCandidate(string directory, string target, string? expectedHash)
    {
        SafeFiles.NoLinks(directory);
        var package = settings.TargetDirectory("desktop");
        if (Path.GetFullPath(directory).Equals(Path.GetFullPath(package), StringComparison.OrdinalIgnoreCase)
            || SafeFiles.Within(directory, package) || SafeFiles.Within(package, directory)
            || SafeFiles.Within(directory, settings.DataRoot))
            throw new ArgumentException("Candidate must be separate from the installed product and data.");
        var entry = Path.GetFileNameWithoutExtension(settings.EntryFile(target));
        foreach (var file in new[] { entry + ".exe", entry + ".dll", entry + ".deps.json", entry + ".runtimeconfig.json",
                     target == "core" ? "wwwroot/admin/index.html" : "core/PuddingAgent.exe",
                     target == "core" ? "wwwroot/admin/index.html" : "core/wwwroot/admin/index.html" })
            if (!File.Exists(SafeFiles.Resolve(directory, file))) throw new InvalidDataException("Incomplete product artifact: " + file);
        if (!string.IsNullOrWhiteSpace(expectedHash) && !SafeFiles.Hash(Path.Combine(directory, entry + ".dll")).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Candidate assembly hash mismatch.");
    }

    private async Task RunAsync(string executable, IEnumerable<string> arguments, string workingDirectory, string logPath,
        Dictionary<string, string>? environment, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(settings.BuildTimeoutSeconds));
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = new System.Text.UTF8Encoding(false), StandardErrorEncoding = new System.Text.UTF8Encoding(false) };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        if (environment is not null) foreach (var (key, value) in environment) info.Environment[key] = value;
        using var process = Process.Start(info) ?? throw new IOException("Build process could not start.");
        await using var log = new StreamWriter(logPath);
        using var logGate = new SemaphoreSlim(1, 1);
        var token = GuardianToken.Read(settings);
        async Task DrainAsync(StreamReader reader)
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                await logGate.WaitAsync();
                try { await log.WriteLineAsync(line.Replace(token, "[redacted]")); }
                finally { logGate.Release(); }
            }
        }
        var output = Task.WhenAll(DrainAsync(process.StandardOutput), DrainAsync(process.StandardError));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await output;
            throw;
        }
        await output;
        if (process.ExitCode != 0) throw new InvalidOperationException("Build failed; see the isolated build log.");
    }
}
