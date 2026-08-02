using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using Color = System.Windows.Media.Color;

namespace PuddingDesktop.Theming;

/// <summary>
/// Reads the Windows application theme once at startup and updates shared design tokens.
/// </summary>
public sealed class WindowsThemeService
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetColorizationColor(out uint colorization, out bool opaqueBlend);

    public bool IsDarkMode { get; }
    public Color AccentColor { get; }

    public WindowsThemeService()
    {
        IsDarkMode = DetectDarkMode();
        AccentColor = DetectAccentColor();
    }

    public void ApplyTo(ResourceDictionary resources)
    {
        var accent = AccentColor;
        var accentHover = Blend(accent, Colors.White, IsDarkMode ? 0.18 : 0.10);
        var accentPressed = Blend(accent, Colors.Black, 0.14);

        Set(resources, "AccentColor", accent);
        Set(resources, "AccentHoverColor", accentHover);
        Set(resources, "AccentPressedColor", accentPressed);

        if (IsDarkMode)
        {
            Set(resources, "WindowBackgroundColor", Color.FromArgb(0xDC, 0x20, 0x20, 0x20));
            Set(resources, "LayerFillColor", Color.FromArgb(0xF7, 0x28, 0x28, 0x28));
            Set(resources, "NavigationFillColor", Color.FromArgb(0xE5, 0x25, 0x25, 0x25));
            Set(resources, "CardFillColor", Color.FromArgb(0xF7, 0x2D, 0x2D, 0x2D));
            Set(resources, "ControlFillColor", Color.FromArgb(0xE6, 0x33, 0x33, 0x33));
            Set(resources, "ControlFillSecondaryColor", Color.FromArgb(0xB3, 0x3A, 0x3A, 0x3A));
            Set(resources, "SubtleFillColor", Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));
            Set(resources, "SubtleFillHoverColor", Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
            Set(resources, "ControlStrokeColor", Color.FromArgb(0x25, 0xFF, 0xFF, 0xFF));
            Set(resources, "DividerColor", Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
            Set(resources, "TextPrimaryColor", Color.FromArgb(0xF5, 0xFF, 0xFF, 0xFF));
            Set(resources, "TextSecondaryColor", Color.FromArgb(0xC5, 0xFF, 0xFF, 0xFF));
            Set(resources, "TextTertiaryColor", Color.FromArgb(0x8F, 0xFF, 0xFF, 0xFF));
            Set(resources, "AccentLightColor", Blend(accent, Color.FromRgb(0x20, 0x20, 0x20), 0.72));
            Set(resources, "SuccessFillColor", Color.FromRgb(0x1D, 0x3A, 0x22));
            Set(resources, "WarningFillColor", Color.FromRgb(0x43, 0x35, 0x17));
            Set(resources, "ErrorFillColor", Color.FromRgb(0x44, 0x22, 0x24));
        }
    }

    private static bool DetectDarkMode()
    {
        try
        {
            const string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(keyPath);
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }

    private static Color DetectAccentColor()
    {
        try
        {
            if (DwmGetColorizationColor(out var value, out _) == 0)
            {
                var color = Color.FromArgb(
                    0xFF,
                    (byte)(value >> 16),
                    (byte)(value >> 8),
                    (byte)value);
                if (color.R + color.G + color.B is > 120 and < 690)
                    return color;
            }
        }
        catch
        {
        }

        return Color.FromRgb(0x00, 0x67, 0xC0);
    }

    private static Color Blend(Color first, Color second, double secondWeight)
    {
        var firstWeight = 1 - secondWeight;
        return Color.FromRgb(
            (byte)((first.R * firstWeight) + (second.R * secondWeight)),
            (byte)((first.G * firstWeight) + (second.G * secondWeight)),
            (byte)((first.B * firstWeight) + (second.B * secondWeight)));
    }

    private static void Set(ResourceDictionary resources, string key, Color value)
        => resources[key] = value;
}
