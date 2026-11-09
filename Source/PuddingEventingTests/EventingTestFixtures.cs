using System.Collections.Immutable;
using Pudding.Api.Contracts;
using Pudding.EventingTestHelpers;
using PuddingEventing;
using PuddingEventing.Contracts;

namespace Pudding.EventingTestHelpers;

internal static class EventingTestFixtures
{
    public static HostPluginBinding Binding(long generation = 1, string workspace = "ws-1",
        params string[] allowedKinds)
        => new("plugin-a", generation, workspace, "work-1", "budget-1",
            allowedKinds.Length == 0 ? ["review.completed"] : [.. allowedKinds], ["audit.write"]);
}
