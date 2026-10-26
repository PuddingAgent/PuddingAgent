using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using PuddingCode.Configuration;
using PuddingDesktop.Bootstrap;
using PuddingDesktop.Configuration;
using PuddingDesktop.Core;
using PuddingDesktop.Debug;
using PuddingDesktop.Diagnostics;

namespace PuddingDesktop.Hosting;

public sealed partial class DesktopApplicationCoordinator
{
    private PuddingDesktopGuardianConfig _guardianConfig = new();
    private bool _guardianManaged;
    private bool _guardianStartHidden;
    private static readonly JsonSerializerOptions GuardianJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private async Task EnsureGuardianAsync(string dataRoot, PuddingDesktopConfig desktop, CancellationToken ct)
    {
        if (!desktop.Guardian.Enabled) return;
        try
        {
            if (_bootstrapSettings!.Debug.Enabled)
                throw new InvalidOperationException("Guardian requires the packaged Core; disable Desktop debug mode before enabling guardian.");
            if (!desktop.Bootstrap.HttpEnabled) throw new InvalidOperationException("Guardian requires the Desktop loopback control endpoint.");
            var executable = Path.Combine(AppContext.BaseDirectory, "guardian", "PuddingDesktop.Guardian.exe");
            if (!File.Exists(executable)) throw new FileNotFoundException("Guardian bundle is missing.", executable);
            string? repository = null;
            try { repository = DebugRepositoryResolver.ResolveRepositoryRoot(desktop.Guardian.RepositoryRoot ?? _bootstrapSettings.Debug.RepositoryRoot); }
            catch (DirectoryNotFoundException) when (desktop.Guardian.RepositoryRoot is null) { }
            var home = DesktopBootstrapPathProvider.GetDirectoryPath();
            var configPath = Path.Combine(home, "guardian.json");
            var settings = new
            {
                desktopExecutablePath = Environment.ProcessPath!,
                coreExecutablePath = CoreExecutableResolver.Resolve(_bootstrapSettings.CoreExecutablePath),
                desktopHome = home, dataRoot, stateDirectory = Path.Combine(home, "guardian"), repositoryRoot = repository,
                desktop.Guardian.HttpPort, desktopControlPort = desktop.Bootstrap.HttpPort,
                desktop.Guardian.Configuration, desktop.Guardian.StartupTimeoutSeconds, desktop.Guardian.StableSeconds,
                desktop.Guardian.PollSeconds, desktop.Guardian.FailureThreshold, desktop.Guardian.BuildTimeoutSeconds,
                shutdownTimeoutSeconds = desktop.Core.ShutdownTimeoutSeconds, autoStartCore = desktop.Core.AutoStart,
                autoYolo = desktop.Bootstrap.AutoYolo,
                startHidden = _guardianStartHidden,
            };
            using var client = await GuardianClientAsync(ct);
            if (!await CheckGuardianAsync(client, dataRoot, ct))
            {
                Directory.CreateDirectory(home);
                var staging = configPath + ".tmp";
                await File.WriteAllTextAsync(staging, JsonSerializer.Serialize(settings, GuardianJson), ct);
                File.Move(staging, configPath, overwrite: true);
                var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory };
                info.ArgumentList.Add("--config"); info.ArgumentList.Add(configPath);
                info.ArgumentList.Add("--adopt-pid"); info.ArgumentList.Add(Environment.ProcessId.ToString());
                using var process = Process.Start(info) ?? throw new IOException("Guardian process failed to start.");
                for (var attempt = 0; attempt < 20 && !_guardianManaged; attempt++)
                {
                    await Task.Delay(250, ct);
                    _guardianManaged = await CheckGuardianAsync(client, dataRoot, ct);
                    if (!_guardianManaged && process.HasExited) break;
                }
            }
            else _guardianManaged = true;
            if (!_guardianManaged) throw new IOException("Guardian did not expose its authenticated endpoint.");
            DesktopDiagnosticLog.Journal?.Record("guardian_connected", $"port={desktop.Guardian.HttpPort}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { DesktopDiagnosticLog.Write("GuardianStart", ex); }
    }

    private async Task<HttpClient> GuardianClientAsync(CancellationToken ct)
    {
        var client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
        { BaseAddress = new Uri($"http://127.0.0.1:{_guardianConfig.HttpPort}"), Timeout = TimeSpan.FromSeconds(2) };
        client.DefaultRequestHeaders.Add("X-Control-Token", await GetDesktopControlTokenAsync(ct));
        return client;
    }

    private static async Task<bool> CheckGuardianAsync(HttpClient client, string dataRoot, CancellationToken ct)
    {
        try
        {
            using var response = await client.GetAsync("/guardian/status", ct);
            if (!response.IsSuccessStatusCode) return false;
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var status = json.RootElement;
            if (status.GetProperty("protocol").GetString() != "pudding.guardian/1"
                || !string.Equals(status.GetProperty("desktopExecutablePath").GetString(), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetFullPath(status.GetProperty("dataRoot").GetString()!), Path.GetFullPath(dataRoot), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Guardian port belongs to another installation.");
            return true;
        }
        catch (HttpRequestException) { return false; }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return false; }
    }

    private async Task<DesktopBootstrapResult> TriggerGuardianBootstrapAsync(string? requestedBy, bool yolo, CancellationToken ct)
    {
        using var client = await GuardianClientAsync(ct);
        using var response = await client.PostAsJsonAsync("/guardian/commands", new {
            requestId = Guid.NewGuid().ToString("N"), action = "build-restart", target = "core", requestedBy, yolo
        }, ct);
        response.EnsureSuccessStatusCode();
        return new DesktopBootstrapResult { Accepted = true, Action = "guardian-build-restart", StartedAt = DateTimeOffset.UtcNow };
    }
}
