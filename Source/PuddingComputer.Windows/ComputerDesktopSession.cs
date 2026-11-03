using OpenCvSharp;
using Pudding.Contracts.Computer;
using PuddingComputer.Automation;

namespace PuddingComputer.Windows;

public sealed record DesktopObservation(string ObservationId, DesktopWindowState Window,
    ComputerImageGeometry Geometry, IReadOnlyList<AnnotatedTarget> Targets, int TotalCandidates,
    DateTimeOffset CapturedAtUtc, DateTimeOffset ExpiresAtUtc, byte[] OriginalPng, byte[] AnnotatedPng);

/// <summary>Bounded, caller-scoped observations shared by observe and input tools.</summary>
public sealed class ComputerDesktopSession
{
    public WindowsDesktopInput Input { get; } = new();
    private readonly IComputerElementDetector _detector;
    private readonly Dictionary<string, Entry> _observations = new();
    private sealed record Entry(string Owner, DesktopObservation Observation, string Layout, byte[] SourcePng);
    public ComputerDesktopSession() : this(new OpenCvElementDetector()) { }
    public ComputerDesktopSession(IComputerElementDetector detector) => _detector = detector;

    public DesktopObservation Observe(string owner, long handle, int maxEdge = 1600, int maxCandidates = 60,
        PhysicalRect? clientRegion = null)
    {
        if (string.IsNullOrWhiteSpace(owner)) throw new ArgumentException("Observation owner is required.");
        if (maxEdge is < 512 or > 2048 || maxCandidates is < 1 or > 100) throw new ArgumentException("maxEdge=512..2048; maxCandidates=1..100.");
        lock (WindowsDesktopInput.Gate)
        {
            using var dpi = WindowsDpiEnforcer.EnterPhysicalCoordinates();
            var window = Input.Inspect(handle);
            if (!window.Visible || window.Minimized) throw new InvalidOperationException("Window is not visible; focus it before observing.");
            var bounds = window.ClientBounds;
            if (clientRegion is { } region)
            {
                if (region.X < 0 || region.Y < 0 || region.Width <= 0 || region.Height <= 0 ||
                    (long)region.X + region.Width > bounds.Width || (long)region.Y + region.Height > bounds.Height)
                    throw new ArgumentException("Crop must be inside the client rectangle in physical pixels.");
                bounds = new(bounds.X + region.X, bounds.Y + region.Y, region.Width, region.Height);
            }
            var monitors = WindowsMonitorEnumerator.Enumerate();
            if (monitors.Count == 0) throw new InvalidOperationException("No interactive displays.");
            var left = Math.Max(bounds.X, monitors.Min(m => m.Bounds.X));
            var top = Math.Max(bounds.Y, monitors.Min(m => m.Bounds.Y));
            var right = Math.Min(bounds.X + bounds.Width, monitors.Max(m => m.Bounds.X + m.Bounds.Width));
            var bottom = Math.Min(bounds.Y + bounds.Height, monitors.Max(m => m.Bounds.Y + m.Bounds.Height));
            bounds = new(left, top, right - left, bottom - top);
            if (bounds.Width <= 0 || bounds.Height <= 0 || (long)bounds.Width * bounds.Height > 16_000_000)
                throw new InvalidOperationException("Window is off-screen or capture exceeds 16 million pixels; request a client crop.");
            using var capture = WindowsScreenCapturer.CaptureRegion(bounds);
            using var source = capture.ToBgr();
            var scale = Math.Min(1d, maxEdge / (double)Math.Max(source.Width, source.Height));
            using var view = new Mat();
            Cv2.Resize(source, view, new Size(Math.Max(1, (int)Math.Round(source.Width * scale)), Math.Max(1, (int)Math.Round(source.Height * scale))),
                interpolation: InterpolationFlags.Area);
            var geometry = new ComputerImageGeometry(bounds, view.Width, view.Height, ComputerCaptureMode.ScreenPixels, true);
            var detections = _detector.Detect(source, bounds, bounds)
                .Where(d => ComputerScreenAnalyzer.IsCenterInside(d.Rect, bounds))
                .OrderBy(d => d.Rect.Y).ThenBy(d => d.Rect.X).ToArray();
            var targets = ComputerSnapshotAnnotator.Annotate(detections.Take(maxCandidates).ToArray(), window.Bounds, geometry);
            using var marked = view.Clone();
            foreach (var target in targets)
            {
                var r = target.Global;
                var x = (int)Math.Round((r.X - bounds.X) * (double)view.Width / bounds.Width);
                var y = (int)Math.Round((r.Y - bounds.Y) * (double)view.Height / bounds.Height);
                var w = Math.Max(1, (int)Math.Round(r.Width * (double)view.Width / bounds.Width));
                var h = Math.Max(1, (int)Math.Round(r.Height * (double)view.Height / bounds.Height));
                Cv2.Rectangle(marked, new Rect(x, y, w, h), new Scalar(0, 210, 255), 1);
                Cv2.PutText(marked, target.Id.ToString(), new Point(Math.Max(0, x), Math.Max(14, y)),
                    HersheyFonts.HersheySimplex, .5, new Scalar(0, 0, 0), 3);
                Cv2.PutText(marked, target.Id.ToString(), new Point(Math.Max(0, x), Math.Max(14, y)),
                    HersheyFonts.HersheySimplex, .5, new Scalar(0, 210, 255), 1);
            }
            var now = DateTimeOffset.UtcNow;
            var observation = new DesktopObservation(Guid.NewGuid().ToString("N"), window, geometry, targets,
                detections.Length, now, now.AddSeconds(45), view.ToBytes(".png"), marked.ToBytes(".png"));
            foreach (var expired in _observations.Where(e => e.Value.Observation.ExpiresAtUtc <= now).Select(e => e.Key).ToArray())
                _observations.Remove(expired);
            while (_observations.Count >= 16) _observations.Remove(_observations.MinBy(e => e.Value.Observation.CapturedAtUtc).Key);
            _observations.Add(observation.ObservationId, new(owner, observation, Layout(monitors), source.ToBytes(".png")));
            return observation;
        }
    }

    public DesktopInputResult ClickObservation(string owner, string observationId, int? targetId,
        ImagePoint? imagePoint, int steps = 12, string button = "left", int clicks = 1, CancellationToken ct = default)
    {
        lock (WindowsDesktopInput.Gate)
        {
            using var dpi = WindowsDpiEnforcer.EnterPhysicalCoordinates();
            if (!_observations.TryGetValue(observationId, out var entry) || entry.Owner != owner)
                return WindowsDesktopInput.Blocked("click", null, "observation_missing_or_wrong_owner");
            var observation = entry.Observation;
            var current = Input.Inspect(observation.Window.Handle);
            var invalid = ValidateObservation(observation, current, DateTimeOffset.UtcNow);
            if (invalid is null && entry.Layout != Layout(WindowsMonitorEnumerator.Enumerate())) invalid = "display_layout_changed";
            if (invalid is not null) return WindowsDesktopInput.Blocked("click", current, invalid);
            if ((targetId is null) == (imagePoint is null)) return WindowsDesktopInput.Blocked("click", current, "choose_target_id_or_image_point");
            if (targetId is { } id)
            {
                var target = observation.Targets.SingleOrDefault(t => t.Id == id);
                if (target is null) return WindowsDesktopInput.Blocked("click", current, "unknown_target_id");
                imagePoint = target.Clickable;
            }
            var mapped = ComputerCoordinateMapper.Map(observation.Geometry, imagePoint!.Value);
            if (!mapped.IsOk) return WindowsDesktopInput.Blocked("click", current, "invalid_image_point");
            if (!AnchorUnchanged(entry, mapped.Point)) return WindowsDesktopInput.Blocked("click", current, "target_pixels_changed_refresh_observation", mapped.Point);
            // A consumed ID is never replayed, including failed/partially delivered clicks.
            _observations.Remove(observationId);
            return Input.Click(current.Handle, mapped.Point, steps, button, clicks, ct);
        }
    }

    public void Invalidate(string owner, long handle)
    {
        lock (WindowsDesktopInput.Gate)
            foreach (var id in _observations.Where(e => e.Value.Observation.Window.Handle == handle).Select(e => e.Key).ToArray())
                _observations.Remove(id);
    }

    internal static string? ValidateObservation(DesktopObservation observation, DesktopWindowState current, DateTimeOffset now)
    {
        if (now >= observation.ExpiresAtUtc) return "observation_expired";
        var old = observation.Window;
        if (old.Handle != current.Handle || old.ProcessId != current.ProcessId || old.ProcessStartTimeUtcTicks != current.ProcessStartTimeUtcTicks)
            return "window_identity_changed";
        if (old.Bounds != current.Bounds || old.ClientBounds != current.ClientBounds || old.Dpi != current.Dpi) return "window_geometry_changed";
        return null;
    }

    private static string Layout(IReadOnlyList<MonitorBounds> monitors) => string.Join(";", monitors
        .OrderBy(m => m.Bounds.X).ThenBy(m => m.Bounds.Y).Select(m => m.ToString()));

    private static bool AnchorUnchanged(Entry entry, ScreenPoint point)
    {
        var bounds = entry.Observation.Geometry.SourceRect;
        var x = Math.Max(0, point.X - bounds.X - 12);
        var y = Math.Max(0, point.Y - bounds.Y - 12);
        var rect = new Rect(x, y, Math.Min(24, bounds.Width - x), Math.Min(24, bounds.Height - y));
        if (rect.Width <= 0 || rect.Height <= 0) return false;
        using var original = Cv2.ImDecode(entry.SourcePng, ImreadModes.Color);
        using var old = new Mat(original, rect);
        using var capture = WindowsScreenCapturer.CaptureRegion(new(bounds.X + x, bounds.Y + y, rect.Width, rect.Height));
        using var current = capture.ToBgr();
        return Cv2.Norm(old, current, NormTypes.L1) / (rect.Width * rect.Height * 3d) <= 3;
    }
}
