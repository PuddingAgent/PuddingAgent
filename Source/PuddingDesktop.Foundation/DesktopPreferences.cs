using System.Text.Json;

namespace PuddingDesktop.Foundation;

public sealed record DesktopLanguage(string Tag, string DisplayName);

/// <summary>
/// DS-01: only languages the desktop actually ships resources for are offered. The UI is Simplified
/// Chinese today, so this lists one real language and says so instead of inventing an untranslated entry.
/// </summary>
public static class DesktopLanguages
{
    public static IReadOnlyList<DesktopLanguage> Supported { get; } = [new("zh-CN", "简体中文")];

    public static string Default => Supported[0].Tag;

    /// <summary>A language switch needs reloaded resources, so it applies after restarting Desktop.</summary>
    public const bool RequiresRestart = true;

    public static bool IsSupported(string? tag) =>
        tag is not null && Supported.Any(language => string.Equals(language.Tag, tag, StringComparison.OrdinalIgnoreCase));

    public static string Normalize(string? tag) => IsSupported(tag)
        ? Supported.First(language => string.Equals(language.Tag, tag, StringComparison.OrdinalIgnoreCase)).Tag
        : Default;

    public static DesktopLanguage Describe(string tag) =>
        Supported.First(language => string.Equals(language.Tag, Normalize(tag), StringComparison.Ordinal));
}

/// <summary>Real desktop preferences: appearance and language. Not Core configuration.</summary>
public sealed record DesktopPreferences(ShellLayout Layout, string Theme = "Light", string Material = "Mica",
    string Language = "zh-CN")
{
    public static DesktopPreferences Default => new(new ShellLayout());

    public DesktopPreferences Normalize() => new((Layout ?? new()).Normalize(),
        Theme is "Light" or "Dark" or "Default" ? Theme : "Light",
        Material is "MicaAlt" or "Acrylic" ? Material : "Mica",
        DesktopLanguages.Normalize(Language));
}

public sealed record PreferencesLoadResult(DesktopPreferences Preferences, string? Warning);

public sealed class DesktopPreferencesStore(string directory)
{
    public string FilePath { get; } = Path.Combine(Path.GetFullPath(directory), "desktop.preferences.json");

    public async Task<PreferencesLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(FilePath)) return new(DesktopPreferences.Default, null);
            await using var file = File.OpenRead(FilePath);
            var preferences = await JsonSerializer.DeserializeAsync<DesktopPreferences>(file, cancellationToken: cancellationToken);
            return new(preferences?.Normalize() ?? DesktopPreferences.Default, preferences is null ? "配置为空，已使用默认偏好。" : null);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return new(DesktopPreferences.Default, "偏好配置无法读取，已使用默认值。原文件未修改。");
        }
    }

    public async Task SaveAsync(DesktopPreferences preferences, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary,
                JsonSerializer.Serialize(preferences.Normalize(), new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
