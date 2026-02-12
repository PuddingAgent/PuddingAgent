using System;
using System.IO;
using CommunityToolkit.Mvvm.Input;

namespace PuddingAssistantDesktop.ViewModels;

/// <summary>
/// Represents a file attached to the chat input, displayed as a removable chip.
/// </summary>
public sealed partial class AttachmentItem(string filePath, Action<AttachmentItem> onRemove)
{
    /// <summary>Full path to the attached file.</summary>
    public string FilePath { get; } = filePath;

    /// <summary>File name for display.</summary>
    public string DisplayName { get; } = Path.GetFileName(filePath);

    /// <summary>Icon based on file extension.</summary>
    public string Icon { get; } = GetIcon(filePath);

    /// <summary>File size in human-readable form.</summary>
    public string SizeText { get; } = GetSizeText(filePath);

    [RelayCommand]
    private void Remove() => onRemove(this);

    private static string GetIcon(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".svg" => "🖼️",
        ".pdf" => "📕",
        ".doc" or ".docx" => "📄",
        ".xls" or ".xlsx" or ".csv" => "📊",
        ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => "📦",
        ".cs" or ".js" or ".ts" or ".py" or ".java" or ".cpp" or ".h" => "💻",
        ".md" or ".txt" => "📝",
        ".json" or ".xml" or ".yaml" or ".yml" => "📋",
        ".mp4" or ".avi" or ".mov" or ".mkv" => "🎬",
        ".mp3" or ".wav" or ".flac" => "🎵",
        _ => "📎"
    };

    private static string GetSizeText(string path)
    {
        try
        {
            var size = new FileInfo(path).Length;
            return size switch
            {
                < 1024 => $"{size} B",
                < 1024 * 1024 => $"{size / 1024.0:F1} KB",
                _ => $"{size / (1024.0 * 1024.0):F1} MB"
            };
        }
        catch
        {
            return "?";
        }
    }
}
