using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using PuddingDesktop.Core;

namespace PuddingDesktop.Debug;

public sealed record FrontendDeployOptions
{
    public required string FrontendWorkingDirectory { get; init; }
    public required string TargetAdminDirectory { get; init; }
    public TimeSpan BuildTimeout { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan InstallTimeout { get; init; } = TimeSpan.FromMinutes(10);
}

public sealed record FrontendDeployResult
{
    public required string TargetAdminDirectory { get; init; }
    public required int CopiedFileCount { get; init; }
    public required bool RanInstall { get; init; }
    public bool BuiltFromSource { get; init; } = true;
    public string? IndexSha256 { get; init; }
}

/// <summary>Raised when another frontend build/deploy already owns the Desktop operation slot.</summary>
public sealed class FrontendDeployInProgressException : InvalidOperationException
{
    public FrontendDeployInProgressException()
        : base("前端构建部署正在进行中，请稍候。")
    {
    }
}

/// <summary>
/// Builds the Admin frontend from source (`pnpm run build`) and deploys the
/// dist artifacts into the Core output directory's wwwroot\admin subtree.
/// Only static files are replaced, so a running Core keeps serving them and
/// the MSBuild dist copy (blocked by locked assemblies while Core runs) is
/// bypassed. Clearing the admin subtree removes stale hashed bundles before
/// copying; nothing outside wwwroot\admin is touched.
/// </summary>
public sealed class FrontendBuildDeployService
{
    private const string DistDirectoryName = "dist";
    private const string DistEntryFileName = "index.html";

    public async Task<FrontendDeployResult> DeployAsync(
        FrontendDeployOptions options,
        CoreProcessLogBuffer logBuffer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logBuffer);
        if (options.BuildTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Build timeout must be positive.");
        if (options.InstallTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Install timeout must be positive.");

        var ranInstall = false;
        if (!Directory.Exists(Path.Combine(options.FrontendWorkingDirectory, "node_modules")))
        {
            logBuffer.Append("[frontend-deploy] node_modules missing; running pnpm install first.");
            await RunPnpmAsync(
                FrontendDevSupervisor.CreateInstallStartInfo(options.FrontendWorkingDirectory),
                options.InstallTimeout,
                "[frontend-install] ",
                logBuffer,
                cancellationToken);
            ranInstall = true;
        }

        logBuffer.Append($"[frontend-deploy] Building frontend in {options.FrontendWorkingDirectory}: pnpm run build");
        await RunPnpmAsync(
            CreateBuildStartInfo(options.FrontendWorkingDirectory),
            options.BuildTimeout,
            "[frontend-build] ",
            logBuffer,
            cancellationToken);

        var distDirectory = Path.Combine(options.FrontendWorkingDirectory, DistDirectoryName);
        return DeployArtifacts(
            distDirectory,
            options.TargetAdminDirectory,
            expectedIndexSha256: null,
            ranInstall,
            builtFromSource: true,
            logBuffer);
    }

    /// <summary>
    /// Deploys an already-built dist directory without invoking pnpm. An optional
    /// index.html SHA-256 fences the caller's artifact identity; the copied entry
    /// file is hashed again after deployment before success is returned.
    /// </summary>
    public FrontendDeployResult DeployPrebuiltArtifacts(
        string distDirectory,
        string targetAdminDirectory,
        string? expectedIndexSha256,
        CoreProcessLogBuffer logBuffer)
        => DeployArtifacts(
            distDirectory,
            targetAdminDirectory,
            expectedIndexSha256,
            ranInstall: false,
            builtFromSource: false,
            logBuffer);

    private FrontendDeployResult DeployArtifacts(
        string distDirectory,
        string targetAdminDirectory,
        string? expectedIndexSha256,
        bool ranInstall,
        bool builtFromSource,
        CoreProcessLogBuffer logBuffer)
    {
        ArgumentNullException.ThrowIfNull(logBuffer);

        var sourceIndexPath = Path.Combine(Path.GetFullPath(distDirectory), DistEntryFileName);
        if (!File.Exists(sourceIndexPath))
            throw new FileNotFoundException($"Frontend artifact has no {DistEntryFileName}: {sourceIndexPath}");

        var sourceIndexSha256 = ComputeSha256(sourceIndexPath);
        if (!string.IsNullOrWhiteSpace(expectedIndexSha256)
            && !string.Equals(
                sourceIndexSha256,
                expectedIndexSha256.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Frontend index SHA-256 mismatch: expected={expectedIndexSha256.Trim()}, " +
                $"actual={sourceIndexSha256}.");
        }

        var copiedFileCount = DeployDistFiles(distDirectory, targetAdminDirectory);
        var deployedIndexSha256 = ComputeSha256(Path.Combine(targetAdminDirectory, DistEntryFileName));
        if (!string.Equals(sourceIndexSha256, deployedIndexSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Deployed frontend index.html does not match the source artifact.");

        logBuffer.Append(
            $"[frontend-deploy] Deployed {copiedFileCount} files to {targetAdminDirectory}; " +
            $"indexSha256={deployedIndexSha256}");
        return new FrontendDeployResult
        {
            TargetAdminDirectory = targetAdminDirectory,
            CopiedFileCount = copiedFileCount,
            RanInstall = ranInstall,
            BuiltFromSource = builtFromSource,
            IndexSha256 = deployedIndexSha256,
        };
    }

    internal static ProcessStartInfo CreateBuildStartInfo(string workingDirectory) => new()
    {
        FileName = "cmd.exe",
        Arguments = "/c pnpm run build",
        WorkingDirectory = workingDirectory,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8,
        CreateNoWindow = true,
    };

    /// <summary>
    /// Replaces the target wwwroot\admin subtree with the dist artifacts and
    /// returns the number of copied files.
    /// </summary>
    internal static int DeployDistFiles(string distDirectory, string targetAdminDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(distDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetAdminDirectory);

        var fullDist = Path.GetFullPath(distDirectory);
        var fullTarget = Path.GetFullPath(targetAdminDirectory);
        if (PathsOverlap(fullDist, fullTarget))
        {
            throw new InvalidOperationException(
                $"Refusing to deploy overlapping frontend source and target directories: " +
                $"source={fullDist}, target={fullTarget}.");
        }

        if (!File.Exists(Path.Combine(fullDist, DistEntryFileName)))
            throw new FileNotFoundException(
                $"Frontend build produced no {DistEntryFileName} under {fullDist}. " +
                "Check the [frontend-build] log lines.");

        // This method recursively deletes the target directory, so only the
        // Core wwwroot\admin subtree is ever accepted.
        var adminSuffix = Path.Combine("wwwroot", "admin");
        if (!fullTarget.EndsWith(adminSuffix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Refusing to deploy: target directory must end with {adminSuffix}, got {fullTarget}.");

        if (Directory.Exists(fullTarget))
            Directory.Delete(fullTarget, recursive: true);
        Directory.CreateDirectory(fullTarget);

        var copiedFileCount = 0;
        foreach (var sourcePath in Directory.EnumerateFiles(fullDist, "*", SearchOption.AllDirectories))
        {
            var targetPath = Path.Combine(fullTarget, Path.GetRelativePath(fullDist, sourcePath));
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(sourcePath, targetPath, overwrite: true);
            copiedFileCount++;
        }

        return copiedFileCount;
    }

    private static bool PathsOverlap(string first, string second)
    {
        var normalizedFirst = Path.TrimEndingDirectorySeparator(first);
        var normalizedSecond = Path.TrimEndingDirectorySeparator(second);
        if (string.Equals(normalizedFirst, normalizedSecond, StringComparison.OrdinalIgnoreCase))
            return true;

        return normalizedFirst.StartsWith(
                   normalizedSecond + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase)
               || normalizedSecond.StartsWith(
                   normalizedFirst + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static async Task RunPnpmAsync(
        ProcessStartInfo startInfo,
        TimeSpan timeout,
        string logPrefix,
        CoreProcessLogBuffer logBuffer,
        CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                logBuffer.Append($"{logPrefix}{e.Data}");
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                logBuffer.Append($"{logPrefix}{e.Data}");
        };

        if (!process.Start())
            throw new InvalidOperationException("The operating system refused to start pnpm.");

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
                $"pnpm did not finish within {timeout.TotalMinutes:0} minutes.");
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"pnpm failed with exit code {process.ExitCode}. " +
                $"Last output:{Environment.NewLine}{logBuffer.GetTail(40)}");
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
