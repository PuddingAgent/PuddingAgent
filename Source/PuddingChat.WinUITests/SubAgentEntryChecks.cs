using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Media;
using PuddingChat.WinUI;

namespace PuddingChat.WinUITests;

public partial class App
{
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static async Task VerifySubAgentEntryAsync(ChatWorkspace control, Fixture fixture)
    {
        var withoutIdentity = new TurnContentView { InspectDelegation = _ => throw new Exception("cannot infer run") };
        withoutIdentity.Update([new("unknown", "delegation", "done", "摘要", 1, "child-session")], "");
        var missing = (Expander)withoutIdentity.Children.Single(); missing.IsExpanded = true;
        Check(!((StackPanel)((ScrollViewer)missing.Content).Content).Children.OfType<Button>().Any(),
            "delegation without exact run identity has no guessed inspector action");
        fixture.ShowDelegation = true;
        await control.SelectRoleAsync("test", fixture.Builder); control.ScrollToLatest(); control.UpdateLayout();
        await UntilAsync(() => Descendants<Expander>(control).Any(e => e.Header?.ToString()?.Contains("子代理") == true));
        var delegation = Descendants<Expander>(control).First(e => e.Header?.ToString()?.Contains("子代理") == true);
        delegation.IsExpanded = true; control.UpdateLayout();
        var open = ((StackPanel)((ScrollViewer)delegation.Content).Content).Children.OfType<Button>().Single();
        ((IInvokeProvider)new ButtonAutomationPeer(open).GetPattern(PatternInterface.Invoke)).Invoke();
        await UntilAsync(() => fixture.Inspected is not null);
        Check(fixture.Inspected == new SubAgentInspectionKey(new("test", "builder"), "session", "exact-child-run"),
            "message delegation opens native inspector with captured role parent session and exact run");
        await control.SelectRoleAsync("test", fixture.Reviewer);
        await UntilAsync(() => VisualTreeHelper.GetOpenPopupsForXamlRoot(control.XamlRoot).Count == 0);
        Check(control.SelectedRole?.AgentId == "reviewer", "role switch closes the old role inspector");
    }
}
