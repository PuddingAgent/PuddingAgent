using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace PuddingCode.Skills.BuiltIn;

/// <summary>
/// Application discovery and launch skills for the desktop spirit.
/// Supports listing installed software and launching applications by name.
/// </summary>
public sealed class AppLauncherSkills
{
    /// <summary>Lists installed applications on the system.</summary>
    [PuddingSkill("List installed applications on the computer. Returns app name, publisher, and version.",
        Group = "AppLauncher", AllowedRoles = [AgentRole.Spirit])]
    public Task<string> ListInstalledApps(
        [SkillParam("Optional keyword filter (e.g. 'chrome', 'visual'). Leave empty to list all.")] string? keyword,
        CancellationToken ct)
    {
        var apps = DiscoverApps();

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            apps = apps
                .Where(a => a.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                         || (a.Publisher?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false))
                .ToList();
        }

        if (apps.Count == 0)
            return Task.FromResult(string.IsNullOrWhiteSpace(keyword)
                ? "No installed applications found."
                : $"No applications matching '{keyword}' found.");

        var sb = new StringBuilder();
        sb.AppendLine($"## 💻 Installed Applications ({apps.Count})");
        sb.AppendLine();

        var grouped = apps.GroupBy(a => a.Publisher ?? "Unknown").OrderBy(g => g.Key);
        foreach (var group in grouped)
        {
            sb.AppendLine($"### {group.Key}");
            foreach (var app in group.OrderBy(a => a.Name))
            {
                var ver = string.IsNullOrEmpty(app.Version) ? "" : $" v{app.Version}";
                var path = app.ExePath is not null ? " ✅" : "";
                sb.AppendLine($"  - **{app.Name}**{ver}{path}");
            }
        }

        return Task.FromResult(sb.ToString());
    }

    /// <summary>Launches an installed application by name.</summary>
    [PuddingSkill("Launch an application by name. Searches installed apps and Start Menu shortcuts.",
        Group = "AppLauncher", AllowedRoles = [AgentRole.Spirit])]
    public Task<string> LaunchApp(
        [SkillParam("Application name or keyword (e.g. 'Notepad', 'Chrome', 'VS Code')")] string appName,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(appName);

        // Strategy 1: Find from discovered apps with exe path
        var apps = DiscoverApps();
        var match = apps
            .Where(a => a.ExePath is not null && File.Exists(a.ExePath))
            .Where(a => a.Name.Contains(appName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(a => a.Name.Length) // prefer shortest/most exact match
            .FirstOrDefault();

        if (match?.ExePath is not null)
        {
            return LaunchProcess(match.ExePath, match.Name);
        }

        // Strategy 2: Search Start Menu shortcuts (Windows)
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var shortcut = FindStartMenuShortcut(appName);
            if (shortcut is not null)
            {
                return LaunchProcess(shortcut, Path.GetFileNameWithoutExtension(shortcut));
            }
        }

        // Strategy 3: Try as a direct command (e.g. "notepad", "calc")
        try
        {
            var psi = new ProcessStartInfo(appName)
            {
                UseShellExecute = true
            };
            Process.Start(psi);
            return Task.FromResult($"🚀 Launched: `{appName}`");
        }
        catch
        {
            return Task.FromResult($"❌ Could not find or launch application: '{appName}'.\nTry `list_installed_apps` to see available applications.");
        }
    }

    // ──── App Discovery ────

    private sealed record AppInfo(string Name, string? Publisher, string? Version, string? ExePath);

    private static List<AppInfo> DiscoverApps()
    {
        var apps = new List<AppInfo>();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            DiscoverFromRegistry(apps);
            DiscoverFromStartMenu(apps);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            DiscoverFromLinuxDesktopFiles(apps);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            DiscoverFromMacApplications(apps);
        }

        // Deduplicate by name
        return apps
            .GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First(a => a.ExePath is not null) ?? g.First())
            .OrderBy(a => a.Name)
            .ToList();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void DiscoverFromRegistry(List<AppInfo> apps)
    {
        var keys = new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        };

        foreach (var keyPath in keys)
        {
            using var baseKey = Registry.LocalMachine.OpenSubKey(keyPath);
            if (baseKey is null) continue;

            foreach (var subKeyName in baseKey.GetSubKeyNames())
            {
                try
                {
                    using var sub = baseKey.OpenSubKey(subKeyName);
                    if (sub is null) continue;

                    var displayName = sub.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(displayName)) continue;

                    // Skip system components and updates
                    var systemComponent = sub.GetValue("SystemComponent");
                    if (systemComponent is int sc && sc == 1) continue;
                    if (displayName.StartsWith("KB", StringComparison.OrdinalIgnoreCase)) continue;

                    var publisher = sub.GetValue("Publisher") as string;
                    var version = sub.GetValue("DisplayVersion") as string;
                    var exePath = ResolveExePath(sub.GetValue("DisplayIcon") as string)
                                  ?? ResolveExePath(sub.GetValue("InstallLocation") as string);

                    apps.Add(new AppInfo(displayName, publisher, version, exePath));
                }
                catch
                {
                    // Skip entries that can't be read
                }
            }
        }
    }

    private static void DiscoverFromStartMenu(List<AppInfo> apps)
    {
        var menuPaths = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu)
        };

        foreach (var menuPath in menuPaths)
        {
            if (!Directory.Exists(menuPath)) continue;

            foreach (var lnk in Directory.GetFiles(menuPath, "*.lnk", SearchOption.AllDirectories))
            {
                var name = Path.GetFileNameWithoutExtension(lnk);
                // Skip uninstallers and readmes
                if (name.Contains("Uninstall", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Readme", StringComparison.OrdinalIgnoreCase))
                    continue;

                apps.Add(new AppInfo(name, null, null, lnk));
            }
        }
    }

    private static void DiscoverFromLinuxDesktopFiles(List<AppInfo> apps)
    {
        var dirs = new[] { "/usr/share/applications", "/usr/local/share/applications",
                           Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local/share/applications") };

        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;

            foreach (var desktop in Directory.GetFiles(dir, "*.desktop"))
            {
                try
                {
                    var lines = File.ReadAllLines(desktop);
                    var name = lines.FirstOrDefault(l => l.StartsWith("Name="))?[5..];
                    var exec = lines.FirstOrDefault(l => l.StartsWith("Exec="))?[5..].Split(' ')[0];
                    if (name is not null)
                        apps.Add(new AppInfo(name, null, null, exec));
                }
                catch { }
            }
        }
    }

    private static void DiscoverFromMacApplications(List<AppInfo> apps)
    {
        var appDirs = new[] { "/Applications", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications") };

        foreach (var dir in appDirs)
        {
            if (!Directory.Exists(dir)) continue;

            foreach (var appBundle in Directory.GetDirectories(dir, "*.app"))
            {
                var name = Path.GetFileNameWithoutExtension(appBundle);
                apps.Add(new AppInfo(name, null, null, appBundle));
            }
        }
    }

    // ──── Helpers ────

    private static Task<string> LaunchProcess(string path, string displayName)
    {
        try
        {
            var psi = new ProcessStartInfo(path)
            {
                UseShellExecute = true
            };
            Process.Start(psi);
            return Task.FromResult($"🚀 Launched: **{displayName}**\n  Path: `{path}`");
        }
        catch (Exception ex)
        {
            return Task.FromResult($"❌ Failed to launch {displayName}: {ex.Message}");
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? ResolveExePath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        // DisplayIcon often has format: "C:\path\to\app.exe,0"
        var path = raw.Split(',')[0].Trim('"', ' ');

        if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
            return path;

        // InstallLocation: look for an exe in the directory
        if (Directory.Exists(path))
        {
            var exe = Directory.GetFiles(path, "*.exe", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(f => !Path.GetFileName(f).Contains("unins", StringComparison.OrdinalIgnoreCase));
            return exe;
        }

        return null;
    }

    private static string? FindStartMenuShortcut(string appName)
    {
        var menuPaths = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu)
        };

        foreach (var menuPath in menuPaths)
        {
            if (!Directory.Exists(menuPath)) continue;

            var match = Directory.GetFiles(menuPath, "*.lnk", SearchOption.AllDirectories)
                .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f)
                    .Contains(appName, StringComparison.OrdinalIgnoreCase));

            if (match is not null) return match;
        }

        return null;
    }
}
