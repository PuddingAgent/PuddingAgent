using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using PuddingAssistantDesktop.ViewModels;

namespace PuddingAssistantDesktop.Controls;

/// <summary>
/// Custom-drawn pudding spirit with Q-squishy body, dot eyes, dynamic shadow,
/// and state-driven color transitions. Renders via Avalonia DrawingContext.
/// </summary>
public sealed class PuddingSpiritControl : Control
{
    private SpiritViewModel? _vm;
    private readonly DispatcherTimer _renderTimer;

    public PuddingSpiritControl()
    {
        // Redraw at ~60 fps to animate breathing
        _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _renderTimer.Tick += (_, _) => InvalidateVisual();
        ClipToBounds = false;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _renderTimer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _renderTimer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _vm = DataContext as SpiritViewModel;
    }

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);

        var vm = _vm;
        if (vm is null) return;

        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 1 || h < 1) return;

        var cx = w / 2;
        var cy = h * 0.45; // body center is slightly above middle to leave room for shadow

        // Body radius (base)
        var baseR = Math.Min(w, h) * 0.32;

        // Apply squash & stretch
        var rx = baseR * vm.StretchX;
        var ry = baseR * vm.SquashY;

        // ── 1. Shadow ──
        DrawShadow(ctx, cx, h * 0.82, rx, vm);

        // ── 2. Body ──
        DrawBody(ctx, cx, cy, rx, ry, vm);

        // ── 3. Eyes ──
        DrawEyes(ctx, cx, cy, baseR, vm);

        // ── 4. Blush (Happy state) ──
        if (vm.State == SpiritState.Happy)
            DrawBlush(ctx, cx, cy, baseR);

        // ── 5. Zzz (Sleeping state) ──
        if (vm.State == SpiritState.Sleeping)
            DrawZzz(ctx, cx, cy, baseR, vm.AnimationPhase);

        // ── 6. Thinking ripple ──
        if (vm.State == SpiritState.Thinking)
            DrawThinkingRipple(ctx, cx, cy, baseR, vm.AnimationPhase);

        // ── 7. Startled sweat drops ──
        if (vm.State == SpiritState.Startled)
            DrawSweatDrop(ctx, cx, cy, baseR, vm.AnimationPhase);

        // ── 8. Falling wind lines ──
        if (vm.State == SpiritState.Falling)
            DrawWindLines(ctx, cx, cy, baseR, vm.AnimationPhase);

        // ── 9. Heartbeat glow pulse ──
        if (vm.HeartbeatGlow > 0.01)
            DrawHeartbeatGlow(ctx, cx, cy, rx, ry, vm);
    }

    // ── Drawing helpers ──

    private static void DrawShadow(DrawingContext ctx, double cx, double sy, double rx, SpiritViewModel vm)
    {
        // Shadow scales inversely with squash (when body squishes down, shadow gets bigger)
        var shadowRx = rx * 1.1 * (2.0 - vm.SquashY);
        var shadowRy = rx * 0.18;
        var shadowOpacity = 0.3 * vm.SquashY; // fainter when stretched up

        var shadowBrush = new RadialGradientBrush
        {
            Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(1.0, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(1.0, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb((byte)(shadowOpacity * 255), 0, 0, 0), 0),
                new GradientStop(Color.FromArgb(0, 0, 0, 0), 1)
            }
        };

        var shadowGeom = new EllipseGeometry(new Rect(cx - shadowRx, sy - shadowRy, shadowRx * 2, shadowRy * 2));
        ctx.DrawGeometry(shadowBrush, null, shadowGeom);
    }

    private static void DrawBody(DrawingContext ctx, double cx, double cy, double rx, double ry, SpiritViewModel vm)
    {
        // Pudding body: main ellipse with radial gradient for subsurface scattering
        var bodyBrush = new RadialGradientBrush
        {
            Center = new RelativePoint(0.45, 0.35, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.4, 0.3, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(0.8, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(0.8, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(vm.GlowColor, 0.0),     // bright center (subsurface glow)
                new GradientStop(vm.BodyColor, 0.55),     // main body color
                new GradientStop(DarkenColor(vm.BodyColor, 0.8), 1.0) // darker edge
            }
        };

        // Outline pen: subtle darker border
        var outlinePen = new Pen(new SolidColorBrush(DarkenColor(vm.BodyColor, 0.65)), 1.5);

        // Pudding body shape using a bezier path for the slightly wobbly bottom
        var geom = CreatePuddingGeometry(cx, cy, rx, ry);
        ctx.DrawGeometry(bodyBrush, outlinePen, geom);

        // Highlight: a small bright ellipse near top-left for specular reflection
        var hlRx = rx * 0.22;
        var hlRy = ry * 0.18;
        var hlCx = cx - rx * 0.25;
        var hlCy = cy - ry * 0.35;
        var hlBrush = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(Color.FromArgb(120, 255, 255, 255), 0),
                new GradientStop(Color.FromArgb(0, 255, 255, 255), 1)
            }
        };
        var hlGeom = new EllipseGeometry(new Rect(hlCx - hlRx, hlCy - hlRy, hlRx * 2, hlRy * 2));
        ctx.DrawGeometry(hlBrush, null, hlGeom);
    }

    /// <summary>Creates a pudding-shaped geometry (rounded top, slightly flared base).</summary>
    private static StreamGeometry CreatePuddingGeometry(double cx, double cy, double rx, double ry)
    {
        var geom = new StreamGeometry();
        using var sgc = geom.Open();

        // Start at the top center
        var top = new Point(cx, cy - ry);
        sgc.BeginFigure(top, true);

        // Right side curve
        sgc.CubicBezierTo(
            new Point(cx + rx * 0.8, cy - ry),       // cp1
            new Point(cx + rx * 1.05, cy - ry * 0.2), // cp2 — slight outward bulge
            new Point(cx + rx, cy + ry * 0.3)          // end — right-mid
        );

        // Bottom right curve (wider base for pudding shape)
        sgc.CubicBezierTo(
            new Point(cx + rx * 0.95, cy + ry * 0.7),
            new Point(cx + rx * 0.7, cy + ry),
            new Point(cx, cy + ry)                      // bottom center
        );

        // Bottom left curve
        sgc.CubicBezierTo(
            new Point(cx - rx * 0.7, cy + ry),
            new Point(cx - rx * 0.95, cy + ry * 0.7),
            new Point(cx - rx, cy + ry * 0.3)           // left-mid
        );

        // Left side curve back to top
        sgc.CubicBezierTo(
            new Point(cx - rx * 1.05, cy - ry * 0.2),
            new Point(cx - rx * 0.8, cy - ry),
            top
        );

        sgc.EndFigure(true);
        return geom;
    }

    private static void DrawEyes(DrawingContext ctx, double cx, double cy, double r, SpiritViewModel vm)
    {
        var eyeSpacing = r * 0.32;
        var eyeY = cy - r * 0.08;
        var eyeR = r * 0.06;

        var eyeBrush = new SolidColorBrush(Color.Parse("#2C2C2C"));

        if (vm.State == SpiritState.Sleeping)
        {
            // Closed eyes: small horizontal lines
            var linePen = new Pen(eyeBrush, 2.0);
            var lineHalf = r * 0.08;
            ctx.DrawLine(linePen,
                new Point(cx - eyeSpacing - lineHalf, eyeY),
                new Point(cx - eyeSpacing + lineHalf, eyeY));
            ctx.DrawLine(linePen,
                new Point(cx + eyeSpacing - lineHalf, eyeY),
                new Point(cx + eyeSpacing + lineHalf, eyeY));
        }
        else if (vm.State == SpiritState.Happy)
        {
            // Happy eyes: small upward arcs (^_^)
            var arcPen = new Pen(eyeBrush, 2.0);
            var arcW = r * 0.09;
            var arcH = r * 0.05;

            DrawSmileArc(ctx, cx - eyeSpacing, eyeY, arcW, arcH, arcPen);
            DrawSmileArc(ctx, cx + eyeSpacing, eyeY, arcW, arcH, arcPen);
        }
        else if (vm.State == SpiritState.Startled)
        {
            // Startled eyes: >_< (X-shaped cross marks)
            var crossPen = new Pen(eyeBrush, 2.0);
            var crossR = r * 0.07;

            DrawCrossEye(ctx, cx - eyeSpacing, eyeY, crossR, crossPen);
            DrawCrossEye(ctx, cx + eyeSpacing, eyeY, crossR, crossPen);
        }
        else
        {
            // Normal dot eyes
            var leftEye = new EllipseGeometry(new Rect(
                cx - eyeSpacing - eyeR, eyeY - eyeR, eyeR * 2, eyeR * 2));
            var rightEye = new EllipseGeometry(new Rect(
                cx + eyeSpacing - eyeR, eyeY - eyeR, eyeR * 2, eyeR * 2));

            ctx.DrawGeometry(eyeBrush, null, leftEye);
            ctx.DrawGeometry(eyeBrush, null, rightEye);
        }
    }

    private static void DrawSmileArc(DrawingContext ctx, double cx, double cy, double w, double h, Pen pen)
    {
        var geom = new StreamGeometry();
        using var sgc = geom.Open();
        sgc.BeginFigure(new Point(cx - w, cy), false);
        sgc.CubicBezierTo(
            new Point(cx - w * 0.5, cy - h * 2),
            new Point(cx + w * 0.5, cy - h * 2),
            new Point(cx + w, cy));
        sgc.EndFigure(false);
        ctx.DrawGeometry(null, pen, geom);
    }

    private static void DrawBlush(DrawingContext ctx, double cx, double cy, double r)
    {
        var blushR = r * 0.12;
        var blushY = cy + r * 0.05;
        var blushSpacing = r * 0.45;
        var blushBrush = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(Color.FromArgb(80, 255, 130, 150), 0),
                new GradientStop(Color.FromArgb(0, 255, 130, 150), 1)
            }
        };

        var left = new EllipseGeometry(new Rect(cx - blushSpacing - blushR, blushY - blushR * 0.6, blushR * 2, blushR * 1.2));
        var right = new EllipseGeometry(new Rect(cx + blushSpacing - blushR, blushY - blushR * 0.6, blushR * 2, blushR * 1.2));
        ctx.DrawGeometry(blushBrush, null, left);
        ctx.DrawGeometry(blushBrush, null, right);
    }

    private static void DrawZzz(DrawingContext ctx, double cx, double cy, double r, double phase)
    {
        var zBrush = new SolidColorBrush(Color.FromArgb(150, 100, 120, 180));

        // Three floating Z characters at different phases
        for (var i = 0; i < 3; i++)
        {
            var p = (phase * 0.3 + i * 1.2) % (Math.PI * 2);
            var floatY = cy - r * 0.6 - r * 0.15 * i - Math.Sin(p) * r * 0.08;
            var floatX = cx + r * 0.4 + i * r * 0.12;
            var fontSize = 8 + i * 2;
            var opacity = (byte)(100 + i * 40);

            var ft = new FormattedText(
                "z",
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Arial", FontStyle.Italic, FontWeight.Bold),
                fontSize,
                new SolidColorBrush(Color.FromArgb(opacity, 100, 120, 180)));

            ctx.DrawText(ft, new Point(floatX, floatY));
        }
    }

    private static void DrawThinkingRipple(DrawingContext ctx, double cx, double cy, double r, double phase)
    {
        // Rotating dots around the body
        var dotCount = 6;
        var orbitR = r * 1.25;
        for (var i = 0; i < dotCount; i++)
        {
            var angle = phase * 1.5 + i * (Math.PI * 2 / dotCount);
            var dx = cx + Math.Cos(angle) * orbitR;
            var dy = cy + Math.Sin(angle) * orbitR * 0.5; // elliptical orbit
            var dotR = 2.5 - i * 0.3;
            var opacity = (byte)(180 - i * 25);

            var dotBrush = new SolidColorBrush(Color.FromArgb(opacity, 245, 197, 99));
            var dotGeom = new EllipseGeometry(new Rect(dx - dotR, dy - dotR, dotR * 2, dotR * 2));
            ctx.DrawGeometry(dotBrush, null, dotGeom);
        }
    }

    private static void DrawCrossEye(DrawingContext ctx, double cx, double cy, double r, Pen pen)
    {
        ctx.DrawLine(pen, new Point(cx - r, cy - r), new Point(cx + r, cy + r));
        ctx.DrawLine(pen, new Point(cx + r, cy - r), new Point(cx - r, cy + r));
    }

    private static void DrawSweatDrop(DrawingContext ctx, double cx, double cy, double r, double phase)
    {
        // Animated sweat drop sliding down from upper-right
        var dropX = cx + r * 0.55;
        var dropBaseY = cy - r * 0.5;
        var slide = (phase * 2.0 % (Math.PI * 2)) / (Math.PI * 2); // 0→1 cycle
        var dropY = dropBaseY + slide * r * 0.3;
        var opacity = (byte)(200 - slide * 150);

        var dropBrush = new SolidColorBrush(Color.FromArgb(opacity, 120, 180, 255));
        var dropGeom = new EllipseGeometry(new Rect(dropX - 2.5, dropY - 4, 5, 8));
        ctx.DrawGeometry(dropBrush, null, dropGeom);
    }

    private static void DrawWindLines(DrawingContext ctx, double cx, double cy, double r, double phase)
    {
        // Horizontal speed lines on both sides during free-fall
        var linePen = new Pen(new SolidColorBrush(Color.FromArgb(100, 180, 200, 255)), 1.5);

        for (var i = 0; i < 4; i++)
        {
            var yOff = (i - 1.5) * r * 0.25;
            var xShift = Math.Sin(phase * 3.0 + i * 1.5) * r * 0.15;
            var lineLen = r * (0.3 + i * 0.1);

            // Left side
            ctx.DrawLine(linePen,
                new Point(cx - r * 0.8 - lineLen + xShift, cy + yOff),
                new Point(cx - r * 0.8 + xShift, cy + yOff));

            // Right side
            ctx.DrawLine(linePen,
                new Point(cx + r * 0.8 - xShift, cy + yOff),
                new Point(cx + r * 0.8 + lineLen - xShift, cy + yOff));
        }
    }

    private static void DrawHeartbeatGlow(DrawingContext ctx, double cx, double cy,
        double rx, double ry, SpiritViewModel vm)
    {
        // Soft radial glow overlay that pulses with the perception beat
        var intensity = vm.HeartbeatGlow;
        var glowR = Math.Max(rx, ry) * (1.2 + intensity * 0.3);
        var alpha = (byte)(intensity * 60);

        var glowBrush = new RadialGradientBrush
        {
            Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(1.0, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(1.0, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(alpha, vm.GlowColor.R, vm.GlowColor.G, vm.GlowColor.B), 0),
                new GradientStop(Color.FromArgb(0, vm.GlowColor.R, vm.GlowColor.G, vm.GlowColor.B), 1)
            }
        };

        var glowGeom = new EllipseGeometry(new Rect(cx - glowR, cy - glowR, glowR * 2, glowR * 2));
        ctx.DrawGeometry(glowBrush, null, glowGeom);
    }

    private static Color DarkenColor(Color c, double factor)
    {
        return Color.FromArgb(c.A,
            (byte)(c.R * factor),
            (byte)(c.G * factor),
            (byte)(c.B * factor));
    }
}
