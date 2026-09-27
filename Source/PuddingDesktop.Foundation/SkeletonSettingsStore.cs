using System.Text.Json;

namespace PuddingDesktop.Foundation;

/// <summary>Isolated preview preferences. These are NOT Desktop/Core production configuration.</summary>
public sealed record SkeletonSettings(ShellLayout Layout, string Theme = "Light", string Material = "Mica")
{
    public static SkeletonSettings Default => new(new ShellLayout());
    public SkeletonSettings Normalize() => new((Layout ?? new()).Normalize(),
        Theme is "Light" or "Dark" or "Default" ? Theme : "Light",
        Material is "MicaAlt" or "Acrylic" ? Material : "Mica");
}

public sealed record SettingsLoadResult(SkeletonSettings Settings, string? Warning);

public sealed class SkeletonSettingsStore(string directory)
{
    public string FilePath { get; } = Path.Combine(Path.GetFullPath(directory), "skeleton.settings.json");

    public async Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(FilePath)) return new(SkeletonSettings.Default, null);
            await using var file = File.OpenRead(FilePath);
            var settings = await JsonSerializer.DeserializeAsync<SkeletonSettings>(file, cancellationToken: cancellationToken);
            return new(settings?.Normalize() ?? SkeletonSettings.Default, settings is null ? "配置为空，已使用默认布局。" : null);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return new(SkeletonSettings.Default, "预览配置无法读取，已使用默认布局。原文件未修改。");
        }
    }

    public async Task SaveAsync(SkeletonSettings settings, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary,
                JsonSerializer.Serialize(settings.Normalize(), new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
