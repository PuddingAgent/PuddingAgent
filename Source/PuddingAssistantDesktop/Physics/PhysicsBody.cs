using System;

namespace PuddingAssistantDesktop.Physics;

/// <summary>
/// A 2D physics body using Verlet integration for the desktop pudding spirit.
/// Tracks position, velocity, grounded state, and squash/stretch from impacts.
/// All coordinates are in screen pixels.
/// </summary>
internal sealed class PhysicsBody
{
    private const double DefaultMass = 1.0;
    private const double DefaultDamping = 0.98;
    private const double DefaultFriction = 0.85;

    /// <summary>Impact velocity threshold that triggers a squash animation.</summary>
    private const double SquashImpactThreshold = 80.0;

    /// <summary>Maximum squash factor (0 = fully squished, 1 = no squash).</summary>
    private const double MaxSquashAmount = 0.45;

    /// <summary>How fast the squash recovers per second (spring-like).</summary>
    private const double SquashRecoverySpeed = 6.0;

    // ── Position (Verlet) ──

    public double X { get; set; }
    public double Y { get; set; }
    public double OldX { get; private set; }
    public double OldY { get; private set; }

    // ── Derived velocity (read-only, computed from position delta) ──

    public double VelocityX => X - OldX;
    public double VelocityY => Y - OldY;
    public double Speed => Math.Sqrt(VelocityX * VelocityX + VelocityY * VelocityY);

    // ── Properties ──

    public double Mass { get; set; } = DefaultMass;

    /// <summary>Velocity damping per step (0–1). Lower = more drag.</summary>
    public double Damping { get; set; } = DefaultDamping;

    /// <summary>Horizontal friction applied when grounded (0–1).</summary>
    public double Friction { get; set; } = DefaultFriction;

    /// <summary>Whether the body is resting on a surface.</summary>
    public bool IsGrounded { get; private set; }

    /// <summary>Whether the body is currently being dragged by the user.</summary>
    public bool IsDragged { get; set; }

    // ── Squash / Stretch (driven by landing impact) ──

    /// <summary>Current squash factor for Y axis (1.0 = normal, &lt;1 = squished).</summary>
    public double SquashY { get; private set; } = 1.0;

    /// <summary>Current stretch factor for X axis (volume conservation).</summary>
    public double StretchX { get; private set; } = 1.0;

    // ── Accumulated force ──

    private double _forceX;
    private double _forceY;

    /// <summary>Creates a physics body at the given screen position.</summary>
    public PhysicsBody(double x, double y)
    {
        X = x;
        Y = y;
        OldX = x;
        OldY = y;
    }

    /// <summary>Adds an instantaneous force (pixels/s²) for the next integration step.</summary>
    public void ApplyForce(double fx, double fy)
    {
        _forceX += fx;
        _forceY += fy;
    }

    /// <summary>Sets a specific velocity by adjusting OldPosition.</summary>
    public void SetVelocity(double vx, double vy)
    {
        OldX = X - vx;
        OldY = Y - vy;
    }

    /// <summary>Teleports the body to a new position without generating velocity.</summary>
    public void Teleport(double x, double y)
    {
        X = x;
        Y = y;
        OldX = x;
        OldY = y;
    }

    /// <summary>
    /// Advances the physics body by one time step using Verlet integration.
    /// </summary>
    /// <param name="dt">Time step in seconds.</param>
    public void Integrate(double dt)
    {
        if (IsDragged) return;

        var dt2 = dt * dt;

        // Acceleration from accumulated force
        var ax = _forceX / Mass;
        var ay = _forceY / Mass;

        // Verlet integration
        var newX = X + (X - OldX) * Damping + ax * dt2;
        var newY = Y + (Y - OldY) * Damping + ay * dt2;

        OldX = X;
        OldY = Y;
        X = newX;
        Y = newY;

        // Apply horizontal friction when grounded
        if (IsGrounded)
        {
            var vx = X - OldX;
            OldX = X - vx * Friction;
        }

        // Clear forces for next frame
        _forceX = 0;
        _forceY = 0;
    }

    /// <summary>
    /// Resolves a collision by placing the body on top of a surface at the given Y,
    /// computing landing impact squash, and marking as grounded.
    /// </summary>
    /// <param name="surfaceY">The Y coordinate of the surface top edge.</param>
    /// <param name="bodyHeight">The visual height of the pudding body.</param>
    public void LandOn(double surfaceY, double bodyHeight)
    {
        var impactVelocity = VelocityY;

        // Place body on surface
        Y = surfaceY - bodyHeight;
        OldY = Y;

        // Cancel downward velocity, keep tiny bounce
        var bounceRatio = 0.2;
        if (impactVelocity > SquashImpactThreshold)
        {
            var bounce = -impactVelocity * bounceRatio;
            OldY = Y - bounce;
        }

        // Calculate squash from impact
        if (impactVelocity > SquashImpactThreshold)
        {
            var normalized = Math.Clamp((impactVelocity - SquashImpactThreshold) / 400.0, 0.0, 1.0);
            SquashY = 1.0 - normalized * MaxSquashAmount;
            StretchX = 1.0 + normalized * MaxSquashAmount * 0.5; // volume conservation
        }

        IsGrounded = true;
    }

    /// <summary>Marks the body as no longer grounded (e.g., platform disappeared).</summary>
    public void Detach()
    {
        IsGrounded = false;
    }

    /// <summary>
    /// Recovers squash/stretch toward neutral (1.0) over time. Call every frame.
    /// </summary>
    /// <param name="dt">Time step in seconds.</param>
    public void RecoverSquash(double dt)
    {
        // Spring-like recovery toward 1.0
        SquashY += (1.0 - SquashY) * SquashRecoverySpeed * dt;
        StretchX += (1.0 - StretchX) * SquashRecoverySpeed * dt;

        // Snap to 1.0 when close enough
        if (Math.Abs(SquashY - 1.0) < 0.005)
        {
            SquashY = 1.0;
            StretchX = 1.0;
        }
    }
}
