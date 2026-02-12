using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using PuddingAssistantDesktop.Physics;
using PuddingAssistantDesktop.ViewModels;

namespace PuddingAssistantDesktop.Views;

/// <summary>
/// Transparent, borderless, topmost window hosting the pudding spirit.
/// Integrates physics engine for gravity, window collision, and throw-on-release.
/// Supports multi-monitor virtual desktop bounds.
/// </summary>
public partial class SpiritWindow : Window
{
    private bool _isDragging;
    private Point _dragStart;

    // ── Physics ──

    private PhysicsWorld? _physics;
    private PhysicsBody? _body;
    private readonly DispatcherTimer _physicsTimer;

    /// <summary>Tracks previous position during drag to compute release velocity.</summary>
    private PixelPoint _prevDragPosition;
    private DateTime _prevDragTime;

    public SpiritWindow()
    {
        InitializeComponent();

        // Position near bottom-right of primary screen
        Opened += OnOpened;

        // Double-tap triggers poke/startle reaction
        DoubleTapped += OnSpiritDoubleTapped;

        // Physics loop at ~60fps
        _physicsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _physicsTimer.Tick += OnPhysicsTick;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        var screen = Screens.Primary;
        if (screen is not null)
        {
            var workArea = screen.WorkingArea;
            Position = new PixelPoint(
                workArea.Right - (int)Width - 60,
                workArea.Bottom - (int)Height - 20);
        }

        InitializePhysics();
    }

    // ── Physics initialization ──

    /// <summary>
    /// Computes the virtual desktop bounding box across all monitors
    /// and initializes the physics world with correct bounds.
    /// </summary>
    private void InitializePhysics()
    {
        // Compute virtual desktop bounds spanning all screens
        var allScreens = Screens.All.ToArray();
        if (allScreens.Length == 0) return;

        var virtualLeft = double.MaxValue;
        var virtualTop = double.MaxValue;
        var virtualRight = double.MinValue;
        var virtualBottom = double.MinValue;

        foreach (var screen in allScreens)
        {
            // Use WorkingArea (excludes taskbar) for bottom boundary
            var work = screen.WorkingArea;
            var bounds = screen.Bounds;

            virtualLeft = Math.Min(virtualLeft, bounds.X);
            virtualTop = Math.Min(virtualTop, bounds.Y);
            virtualRight = Math.Max(virtualRight, bounds.Right);
            // Use working area bottom so the spirit lands above the taskbar
            virtualBottom = Math.Max(virtualBottom, work.Bottom);
        }

        _body = new PhysicsBody(Position.X, Position.Y);
        _physics = new PhysicsWorld(_body);

        var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        _physics.Configure(virtualLeft, virtualTop, virtualRight, virtualBottom, handle);
        _physics.SetWindowSize(Width, Height);
        _physics.RefreshPlatforms();

        _physics.OnLanded += OnPhysicsLanded;
        _physicsTimer.Start();
    }

    private void OnPhysicsTick(object? sender, EventArgs e)
    {
        if (_physics is null || _body is null) return;

        var vm = DataContext as SpiritViewModel;

        // Sync drag state
        _body.IsDragged = _isDragging;

        if (_isDragging)
        {
            // During drag: sync physics body to window position
            _body.Teleport(Position.X, Position.Y);
            return;
        }

        // Enter falling state when not grounded and moving down
        if (vm is not null && !_body.IsGrounded && _body.VelocityY > 30)
        {
            vm.EnterFalling();
        }

        // Step physics
        var dt = 1.0 / 60.0;
        _physics.Update(dt);

        // Sync physics squash to ViewModel
        if (vm is not null)
        {
            vm.SetPhysicsSquash(_body.SquashY, _body.StretchX);
        }

        // Update window position from physics body
        Position = new PixelPoint((int)_body.X, (int)_body.Y);
    }

    private void OnPhysicsLanded()
    {
        if (DataContext is SpiritViewModel vm)
        {
            vm.ExitFalling();
        }
    }

    // ── Drag to move (with throw velocity) ──

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _isDragging = true;
            _dragStart = e.GetPosition(this);
            _prevDragPosition = Position;
            _prevDragTime = DateTime.UtcNow;
            e.Pointer.Capture(this);

            if (DataContext is SpiritViewModel vm)
            {
                vm.IsDragging = true;
                vm.TouchInteraction();
            }
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (_isDragging)
        {
            // Track velocity for throw
            _prevDragPosition = Position;
            _prevDragTime = DateTime.UtcNow;

            var currentPos = e.GetPosition(this);
            var delta = currentPos - _dragStart;
            Position = new PixelPoint(
                Position.X + (int)delta.X,
                Position.Y + (int)delta.Y);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (_isDragging)
        {
            _isDragging = false;
            e.Pointer.Capture(null);

            // Compute release velocity for throw effect
            if (_body is not null)
            {
                var elapsed = (DateTime.UtcNow - _prevDragTime).TotalSeconds;
                if (elapsed > 0.001 && elapsed < 0.2)
                {
                    var vx = (Position.X - _prevDragPosition.X) / elapsed;
                    var vy = (Position.Y - _prevDragPosition.Y) / elapsed;

                    // Clamp throw speed
                    var maxThrow = 600.0;
                    vx = Math.Clamp(vx, -maxThrow, maxThrow);
                    vy = Math.Clamp(vy, -maxThrow, maxThrow);

                    _body.Teleport(Position.X, Position.Y);
                    _body.SetVelocity(vx / 60.0, vy / 60.0);
                    _body.Detach();
                }
                else
                {
                    // Gentle drop if released without momentum
                    _body.Teleport(Position.X, Position.Y);
                    _body.SetVelocity(0, 0);
                    _body.Detach();
                }
            }

            if (DataContext is SpiritViewModel vm)
                vm.IsDragging = false;
        }
    }

    // ── Hover detection ──

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        if (DataContext is SpiritViewModel vm)
            vm.IsPointerOver = true;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (DataContext is SpiritViewModel vm)
            vm.IsPointerOver = false;
    }

    // ── Double-click: poke reaction (startle) ──

    private void OnSpiritDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is SpiritViewModel vm)
        {
            vm.Startle();

            // Give a small upward bounce on poke
            if (_body is not null)
            {
                _body.SetVelocity(_body.VelocityX, -5.0);
                _body.Detach();
            }
        }
    }
}
