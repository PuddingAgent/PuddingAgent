using System.Diagnostics;
using System.Text;
using PuddingDesktop.Core;

namespace PuddingDesktop.Debug;

/// <summary>
/// Builds the Core backend from source for debug mode and returns the built
/// apphost executable (bin output includes the MSBuild-assembled
/// wwwroot\admin). The returned exe is started by the regular
/// CoreProcessSupervisor with the unchanged desktop-child protocol, so
/// supervision, Ready handshake, health probes and graceful shutdown all
/// stay in place.
/// </summary>
public sealed class DebugBackendLauncher
{
    private const string OutputExecutableName = "PuddingAgent.exe";
    private const string PrimaryOutputFramework = "net10.0";

    public async Task<string> BuildAsync(
        string backendProjectPath,
        TimeSpan timeout,
        CoreProcessLogBuffer logBuffer,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backendProjectPath);
        ArgumentNullException.ThrowIfNull(logBuffer);
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        var startInfo = CreateBuildStartInfo(backendProjectPath);
        logBuffer.Append($"[Debug] Building backend: {startInfo.FileName} {startInfo.Arguments}");

        using var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                logBuffer.Append($"[build] {e.Data}");
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                logBuffer.Append($"[build] {e.Data}");
        };

        if (!process.Start())
            throw new InvalidOperationException("The operating system refused to start dotnet build.");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(cancellationToken).WaitAsync(timeout, CancellationToken.None);
        }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException(
                $"Backend build did not finish within {timeout.TotalSeconds:0} seconds.");
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Backend build failed with exit code {process.ExitCode}. " +
                $"Last output:{Environment.NewLine}{logBuffer.GetTail(40)}");

        var executablePath = ResolveOutputExecutable(backendProjectPath);
        logBuffer.Append($"[Debug] Backend build output: {executablePath}");
        return executablePath;
    }

    internal static ProcessStartInfo CreateBuildStartInfo(string backendProjectPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"build \"{Path.GetFullPath(backendProjectPath)}\" -v minimal --nologo",
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(backendProjectPath))
                ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        return startInfo;
    }

    /// <summary>
    /// Resolves the primary framework output directory under bin\Debug where
    /// the next debug build will place the exe, without requiring a build.
    /// </summary>
    internal static string ResolveOutputDirectory(string backendProjectPath)
    {
        var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(backendProjectPath))!;
        return Path.Combine(projectDirectory, "bin", "Debug", PrimaryOutputFramework);
    }

    /// <summary>
    /// Resolves the built apphost exe: prefer the primary framework output,
    /// then the newest net* output directory under bin\Debug.
    /// </summary>
    internal static string ResolveOutputExecutable(string backendProjectPath)
    {
        var primary = Path.Combine(ResolveOutputDirectory(backendProjectPath), OutputExecutableName);
        if (File.Exists(primary))
            return primary;

        var debugRoot = Path.GetDirectoryName(ResolveOutputDirectory(backendProjectPath))!;
        if (Directory.Exists(debugRoot))
        {
            var candidate = Directory
                .GetFiles(debugRoot, OutputExecutableName, SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (candidate is not null)
                return candidate;
        }

        throw new FileNotFoundException(
            $"Backend build produced no {OutputExecutableName} under {debugRoot}. " +
            "Check the build output in the debug log.");
    }
}
