using System.Text.Json.Serialization;

namespace PuddingDesktop.Runtime;

[JsonConverter(typeof(JsonStringEnumConverter<DesktopCloseBehavior>))]
public enum DesktopCloseBehavior
{
    MinimizeToTray,
    ExitAndStopCore,
}
