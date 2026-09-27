namespace PuddingDesktop.Foundation;

/// <summary>
/// DS-00 decision: the native client adds no login page and no new principal. Settings operations run
/// as the local, single-user Desktop identity that native chat already uses. It is a compile-time
/// constant, never derived from a text box, command-line argument or HTTP ingress, and it is not a
/// Web Admin role: remote API authorization stays independent and unchanged.
/// </summary>
public static class LocalDesktopIdentity
{
    /// <summary>Matches Core's local single-user session ownership.</summary>
    public const string UserId = "single-user";

    /// <summary>Ordinal match only: "Single-User" or a padded value must never be accepted as the local owner.</summary>
    public static bool Owns(string? ownerId) => string.Equals(ownerId, UserId, StringComparison.Ordinal);
}
