using System;
using System.Collections.Generic;

namespace PuddingAssistantDesktop.Physics;

/// <summary>
/// Manages the 2D physics simulation for the desktop pudding spirit.
/// Applies gravity, performs Verlet integration, detects collisions against
/// desktop window platforms, and resolves landing/bouncing.
/// </summary>
internal sealed class PhysicsWorld
{
    /// <summary>Gravity acceleration in pixels/s².</summary>
    private const double Gravity = 900.0;

    /// <summary>Maximum fall speed to prevent tunneling through thin platforms.</summary>
    private const double MaxFallSpeed = 800.0;

    /// <summary>How often to refresh the platform list (seconds).</summary>
    private const double PlatformRefreshInterval = 0.5;

    private readonly PhysicsBody _body;
    private List<Platform> _platforms = [];
    private double _platformRefreshTimer;

    // ── Virtual desktop bounds (spans all monitors) ──

    private double _virtualLeft;
    private double _virtualTop;
    private double _virtualRight;
    private double _virtualBottom;
    private IntPtr _excludeWindowHandle;

    // ── Spirit window dimensions (set from actual XAML size) ──

    private double _windowWidth = 200.0;
    private double _windowHeight = 220.0;

    /// <summary>Whether physics simulation is currently active.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>The physics body being simulated.</summary>
    public PhysicsBody Body => _body;

    /// <summary>The current platform list (for debug visualization).</summary>
    public IReadOnlyList<Platform> Platforms => _platforms;

    /// <summary>Fired when the body lands on a platform after falling.</summary>
    public event Action? OnLanded;

    public PhysicsWorld(PhysicsBody body)
    {
        _body = body;
    }

    /// <summary>
    /// Configures the virtual desktop bounds that span all monitors and the spirit window handle.
    /// </summary>
    /// <param name="virtualLeft">Leftmost X coordinate across all screens (can be negative).</param>
    /// <param name="virtualTop">Topmost Y coordinate across all screens (can be negative).</param>
    /// <param name="virtualRight">Rightmost X coordinate across all screens.</param>
    /// <param name="virtualBottom">Bottommost Y coordinate across all screens.</param>
    /// <param name="excludeWindowHandle">Handle of the spirit window itself.</param>
    public void Configure(
        double virtualLeft, double virtualTop,
        double virtualRight, double virtualBottom,
        IntPtr excludeWindowHandle)
    {
        _virtualLeft = virtualLeft;
        _virtualTop = virtualTop;
        _virtualRight = virtualRight;
        _virtualBottom = virtualBottom;
        _excludeWindowHandle = excludeWindowHandle;
    }

    /// <summary>Sets the actual spirit window pixel size for accurate collision.</summary>
    public void SetWindowSize(double width, double height)
    {
        _windowWidth = width;
        _windowHeight = height;
    }

    /// <summary>
    /// Advances the physics simulation by one time step.
    /// </summary>
    /// <param name="dt">Time step in seconds (typically 1/60).</param>
    public void Update(double dt)
    {
        if (!IsEnabled || _body.IsDragged) return;

        // Periodically refresh platform list
        _platformRefreshTimer += dt;
        if (_platformRefreshTimer >= PlatformRefreshInterval)
        {
            _platformRefreshTimer = 0;
            RefreshPlatforms();
        }

        // Apply gravity
        _body.ApplyForce(0, Gravity * _body.Mass);

        // Integrate
        _body.Integrate(dt);

        // Clamp fall speed
        ClampVelocity();

        // Collision detection & resolution
        ResolveCollisions();

        // Screen boundary clamping
        ClampToScreen();

        // Recover squash/stretch
        _body.RecoverSquash(dt);
    }

    /// <summary>Refreshes the desktop window platform list.</summary>
    public void RefreshPlatforms()
    {
        if (_virtualRight <= _virtualLeft) return;
        _platforms = DesktopCollider.EnumeratePlatforms(
            _virtualLeft, _virtualTop, _virtualRight, _virtualBottom,
            _excludeWindowHandle);
    }

    private void ClampVelocity()
    {
        var vy = _body.VelocityY;
        if (vy > MaxFallSpeed)
        {
            _body.SetVelocity(_body.VelocityX, MaxFallSpeed);
        }
    }

    private void ResolveCollisions()
    {
        var wasGrounded = _body.IsGrounded;

        // Body edges in screen coordinates (Y = window top, body bottom = Y + windowHeight)
        var bodyBottom = _body.Y + _windowHeight;
        var bodyLeft = _body.X;
        var bodyRight = _body.X + _windowWidth;

        var landed = false;

        foreach (var platform in _platforms)
        {
            // Check horizontal overlap
            if (bodyRight < platform.Left || bodyLeft > platform.Right)
                continue;

            // Check if body is falling onto the platform from above
            if (bodyBottom >= platform.Top && bodyBottom <= platform.Bottom + 20)
            {
                // Only land if moving downward
                if (_body.VelocityY >= 0)
                {
                    _body.LandOn(platform.Top, _windowHeight);
                    landed = true;
                    break;
                }
            }
        }

        if (!landed && wasGrounded)
        {
            // Check if still above the platform we were standing on
            var stillSupported = false;
            foreach (var platform in _platforms)
            {
                if (bodyRight < platform.Left || bodyLeft > platform.Right)
                    continue;

                var distToTop = Math.Abs(bodyBottom - platform.Top);
                if (distToTop < 10)
                {
                    stillSupported = true;
                    break;
                }
            }

            if (!stillSupported)
            {
                _body.Detach();
            }
        }

        if (landed && !wasGrounded)
        {
            OnLanded?.Invoke();
        }
    }

    private void ClampToScreen()
    {
        // Left edge (can be negative on multi-monitor)
        if (_body.X < _virtualLeft)
        {
            var vy = _body.VelocityY;
            _body.Teleport(_virtualLeft, _body.Y);
            _body.SetVelocity(0, vy);
        }

        // Right edge
        var maxX = _virtualRight - _windowWidth;
        if (_body.X > maxX)
        {
            var vy = _body.VelocityY;
            _body.Teleport(maxX, _body.Y);
            _body.SetVelocity(0, vy);
        }

        // Top edge (can be negative on multi-monitor)
        if (_body.Y < _virtualTop)
        {
            var vx = _body.VelocityX;
            _body.Teleport(_body.X, _virtualTop);
            _body.SetVelocity(vx, 0);
        }
    }
}
