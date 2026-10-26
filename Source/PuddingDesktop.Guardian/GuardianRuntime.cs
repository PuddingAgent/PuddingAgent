using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace PuddingDesktop.Guardian;

public sealed class GuardianRuntime : IGuardianRuntime, IDisposable
{
    private readonly GuardianSettings _settings;
    private readonly HttpClient _http = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    public ProcessIdentity? Desktop { get; private set; }
    public ProcessIdentity? Core { get; private set; }

    public GuardianRuntime(GuardianSettings settings, GuardianState previous, int? adoptPid)
    {
        _settings = settings;
        Desktop = adoptPid.HasValue ? Identify(adoptPid.Value, settings.DesktopExecutablePath) : previous.Desktop;
        if (adoptPid.HasValue && Desktop is null) throw new InvalidOperationException("Desktop adoption identity mismatch.");
        Desktop ??= FindRecordedDesktop();
        if (Desktop is not null && !IsAlive(Desktop)) Desktop = FindRecordedDesktop();
        Core = previous.Core;
        RefreshCoreChild();
    }

    public async Task<GuardianHealth> ProbeAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Min(4, _settings.PollSeconds + 1)));
        try
        {
            using var request = Request(HttpMethod.Get, "/desktop/guardian/health");
            using var response = await _http.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode) return new(false, false, HasIntentionalExit());
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            var value = json.RootElement;
            var desktop = Identify(value.GetProperty("desktopProcessId").GetInt32(), _settings.DesktopExecutablePath);
            if (desktop is null) return new(false, false, false, "desktop_identity_mismatch");
            Desktop = desktop;
            var diagnostics = value.GetProperty("diagnostics");
            if (!Path.GetFullPath(diagnostics.GetProperty("dataRoot").GetString()!).Equals(Path.GetFullPath(_settings.DataRoot), StringComparison.OrdinalIgnoreCase))
                return new(false, false, false, "data_root_mismatch");
            if (diagnostics.TryGetProperty("coreProcessId", out var pid) && pid.ValueKind == JsonValueKind.Number)
            {
                var candidate = Identify(pid.GetInt32(), _settings.CoreExecutablePath);
                if (candidate is not null && ParentProcessId(candidate.Id) == Desktop.Id) Core = candidate;
            }
            if (HasIntentionalExit() || diagnostics.GetProperty("userStopRequested").GetBoolean()) return new(true, false, true);
            if (Core is null || !IsAlive(Core) || diagnostics.GetProperty("coreState").GetString() != "Ready") return new(true, false);
            if (!Uri.TryCreate(diagnostics.GetProperty("coreAddress").GetString(), UriKind.Absolute, out var address)
                || !address.IsLoopback || address.Scheme != "http") return new(true, false);
            using var ready = await _http.GetAsync(new Uri(address, "/health/ready"), timeout.Token);
            return new(true, ready.IsSuccessStatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            ct.ThrowIfCancellationRequested();
            return new(false, false, HasIntentionalExit(), "health_unavailable");
        }
    }

    public async Task StopAsync(string target, CancellationToken ct)
    {
        RefreshCoreChild();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(_settings.ShutdownTimeoutSeconds));
            using var request = Request(HttpMethod.Post, target == "desktop" ? "/desktop/guardian/exit" : "/desktop/bootstrap/core/stop");
            request.Content = JsonContent.Create(new { });
            using var response = await _http.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode && response.StatusCode == System.Net.HttpStatusCode.Conflict)
                throw new InvalidOperationException("Desktop is busy with another lifecycle operation.");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { ct.ThrowIfCancellationRequested(); }
        await WaitOrTerminateAsync(target == "desktop" ? Desktop : Core, ct);
        if (target == "desktop") await WaitOrTerminateAsync(Core, ct);
    }

    public async Task StartAsync(string target, bool yolo, CancellationToken ct)
    {
        if (Desktop is null || !IsAlive(Desktop))
        {
            var info = new ProcessStartInfo(_settings.DesktopExecutablePath)
            {
                UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(_settings.DesktopExecutablePath)!,
                CreateNoWindow = true,
            };
            info.Environment["PUDDING_DESKTOP_HOME"] = _settings.DesktopHome;
            info.ArgumentList.Add("--guardian-managed");
            using var process = Process.Start(info) ?? throw new IOException("Could not start Desktop.");
            Desktop = new(process.Id, process.StartTime.ToUniversalTime(), _settings.DesktopExecutablePath);
            Core = null;
            return; // Desktop owns Core auto-start. Readiness is accepted separately.
        }
        if (target == "core" || Core is null || !IsAlive(Core))
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(_settings.StartupTimeoutSeconds));
            using var request = Request(HttpMethod.Post, "/desktop/bootstrap/core/start");
            request.Content = JsonContent.Create(new { yolo });
            using var response = await _http.SendAsync(request, timeout.Token);
            response.EnsureSuccessStatusCode();
        }
    }

    public Task<string> PrepareAsync(GuardianCommand command, CancellationToken ct) => new GuardianBuilder(_settings).PrepareAsync(command, ct);

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, $"http://127.0.0.1:{_settings.DesktopControlPort}{path}");
        request.Headers.Add("X-Control-Token", GuardianToken.Read(_settings));
        return request;
    }

    public static ProcessIdentity? Identify(int pid, string expectedExecutable)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            var path = process.MainModule?.FileName;
            return !process.HasExited && string.Equals(Path.GetFullPath(path!), Path.GetFullPath(expectedExecutable), StringComparison.OrdinalIgnoreCase)
                ? new(pid, process.StartTime.ToUniversalTime(), Path.GetFullPath(path!)) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { return null; }
    }
    public static bool IsAlive(ProcessIdentity identity) => Identify(identity.Id, identity.ExecutablePath) is { } found && found.StartTimeUtc == identity.StartTimeUtc;

    private async Task WaitOrTerminateAsync(ProcessIdentity? identity, CancellationToken ct)
    {
        if (identity is null || !IsAlive(identity)) return;
        using var process = Process.GetProcessById(identity.Id);
        try { await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(_settings.ShutdownTimeoutSeconds), ct); }
        catch (TimeoutException)
        {
            // PID reuse is checked again immediately before terminating only the owned process.
            if (IsAlive(identity)) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(10), ct);
        }
    }

    private void RefreshCoreChild()
    {
        if (Desktop is null || !IsAlive(Desktop)) return;
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(_settings.CoreExecutablePath)))
        {
            using (process)
                if (ParentProcessId(process.Id) == Desktop.Id && Identify(process.Id, _settings.CoreExecutablePath) is { } child) Core = child;
        }
    }

    private bool HasIntentionalExit()
    {
        if (Desktop is null) return false;
        var root = Path.Combine(_settings.DesktopHome, "logs", "desktop-runs");
        if (!Directory.Exists(root)) return false;
        foreach (var directory in Directory.EnumerateDirectories(root, $"*-{Desktop.Id}-*"))
        {
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "state.json")));
                var value = json.RootElement;
                if (value.GetProperty("process").GetProperty("startedUtc").GetDateTime().ToUniversalTime() != Desktop.StartTimeUtc) continue;
                var reason = value.GetProperty("exitReason").GetString();
                if (reason is "user_exit" or "window_close_policy" or "session_ending" or "windows_session_end") return true;
            }
            catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException) { }
        }
        return false;
    }

    private ProcessIdentity? FindRecordedDesktop()
    {
        var root = Path.Combine(_settings.DesktopHome, "logs", "desktop-runs");
        if (!Directory.Exists(root)) return null;
        var matches = new List<ProcessIdentity>();
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "state.json")));
                var value = json.RootElement.GetProperty("process");
                var identity = Identify(value.GetProperty("processId").GetInt32(), _settings.DesktopExecutablePath);
                if (identity is not null && identity.StartTimeUtc == value.GetProperty("startedUtc").GetDateTime().ToUniversalTime()) matches.Add(identity);
            }
            catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException) { }
        }
        return matches.Distinct().SingleOrDefault(); // Ambiguous ownership fails closed.
    }

    private static int ParentProcessId(int id)
    {
        if (!OperatingSystem.IsWindows()) return -1;
        using var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid) return -1;
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), Executable = "" };
        if (!Process32First(snapshot, ref entry)) return -1;
        do { if (entry.ProcessId == id) return (int)entry.ParentProcessId; } while (Process32Next(snapshot, ref entry));
        return -1;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId; public nuint DefaultHeapId; public uint ModuleId, Threads, ParentProcessId;
        public int BasePriority; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Executable;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(Microsoft.Win32.SafeHandles.SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(Microsoft.Win32.SafeHandles.SafeFileHandle snapshot, ref ProcessEntry entry);
    public void Dispose() => _http.Dispose();
}
