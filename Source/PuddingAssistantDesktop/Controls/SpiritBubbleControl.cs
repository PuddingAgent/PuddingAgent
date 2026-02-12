using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using PuddingAssistantDesktop.ViewModels;

namespace PuddingAssistantDesktop.Controls;

/// <summary>
/// Custom-drawn speech bubble that floats above the pudding spirit.
/// Renders a rounded rectangle with a small triangle tail pointing downward.
/// Driven by <see cref="SpiritViewModel.BubbleText"/>.
/// </summary>
public sealed class SpiritBubbleControl : Control
{
    private static readonly Color BubbleBg = Color.FromArgb(220, 40, 40, 50);
    private static readonly Color BubbleBorder = Color.FromArgb(180, 160, 200, 220);
    private static readonly Color TextColor = Color.FromArgb(240, 240, 245, 255);

    private const double CornerRadius = 10.0;
    private const double Padding = 10.0;
    private const double TailHeight = 8.0;
    private const double MaxBubbleWidth = 180.0;
    private const double FontSize = 12.0;

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);

        if (DataContext is not SpiritViewModel vm) return;
        if (string.IsNullOrEmpty(vm.BubbleText)) return;

        var text = vm.BubbleText;
        var opacity = vm.BubbleOpacity;
        if (opacity < 0.01) return;

        // Measure text
        var typeface = new Typeface("Inter", FontStyle.Normal, FontWeight.Normal);
        var ft = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            FontSize,
            new SolidColorBrush(TextColor));
        ft.MaxTextWidth = MaxBubbleWidth - Padding * 2;

        var textW = Math.Min(ft.Width, MaxBubbleWidth - Padding * 2);
        var textH = ft.Height;

        var bubbleW = textW + Padding * 2;
        var bubbleH = textH + Padding * 2;

        // Position: centered above the spirit, with tail below
        var cx = Bounds.Width / 2;
        var bubbleX = cx - bubbleW / 2;
        var bubbleY = Bounds.Height - bubbleH - TailHeight - 4;

        using var opacityState = ctx.PushOpacity(opacity);

        // Draw bubble body
        var bubbleRect = new RoundedRect(
            new Rect(bubbleX, bubbleY, bubbleW, bubbleH),
            CornerRadius);
        ctx.DrawRectangle(
            new SolidColorBrush(BubbleBg),
            new Pen(new SolidColorBrush(BubbleBorder), 1.0),
            bubbleRect);

        // Draw tail triangle
        var tailGeom = new StreamGeometry();
        using (var sgc = tailGeom.Open())
        {
            sgc.BeginFigure(new Point(cx - 6, bubbleY + bubbleH), true);
            sgc.LineTo(new Point(cx, bubbleY + bubbleH + TailHeight));
            sgc.LineTo(new Point(cx + 6, bubbleY + bubbleH));
            sgc.EndFigure(true);
        }
        ctx.DrawGeometry(new SolidColorBrush(BubbleBg), null, tailGeom);

        // Draw text
        ctx.DrawText(ft, new Point(bubbleX + Padding, bubbleY + Padding));
    }
}
