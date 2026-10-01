namespace Pudding.Contracts.Desktop;

public enum DesktopNotificationPriority
{
    Low,
    Normal,
    High,
}

public sealed record DesktopNotificationRequest
{
    public const int MaxTitleLength = 128;

    public const int MaxMessageLength = 1024;

    public DesktopNotificationRequest(
        string title,
        string message,
        DesktopNotificationPriority priority = DesktopNotificationPriority.Normal)
    {
        Title = ContractText.RequireDisplayText(title, MaxTitleLength, nameof(title));
        Message = ContractText.RequireDisplayText(message, MaxMessageLength, nameof(message));
        Priority = priority;
    }

    public string Title { get; }

    public string Message { get; }

    public DesktopNotificationPriority Priority { get; }
}

public sealed record DesktopNotificationResult(bool Shown, string? NotificationId);
