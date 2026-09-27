using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using PuddingChat.WinUI;

namespace PuddingChat.WinUITests;

public partial class App
{
    private static async Task VerifyRoleAccessibilityAsync(ChatWorkspace control)
    {
        control.SetActive(false); // Keep the status fixture stable while examining automation peers.
        try
        {
            var roles = Descendants<ListView>(control).Single();
            control.UpdateLayout();
            var card = roles.Items.Cast<RoleAvatarCard>().First();
            await UntilAsync(() => roles.ContainerFromItem(card) is ListViewItem);
            var item = (ListViewItem)roles.ContainerFromItem(card);
            var peer = new ListViewItemAutomationPeer(item);
            Check(AutomationProperties.GetName(roles) == "角色列表" && peer.GetName() == card.AccessibleLabel,
                "realized role item exposes role and current status to automation");
            card.SetStatus(new(card.Agent.AgentId, "running", "正在编译", 3));
            Check(peer.GetName().Contains("正在编译") && peer.GetName().Contains("3 条未读")
                && peer.GetHelpText() == (card.Agent.Description ?? ""),
                "status updates refresh the focused list item automation name and description");
            card.SetStatus(new(card.Agent.AgentId, "idle", "待命", 0));
            var listPeer = FrameworkElementAutomationPeer.CreatePeerForElement(roles);
            var dataPeer = listPeer.GetChildren().First(p => p.GetName() == peer.GetName());
            Check(peer.GetName().Contains("待命") && !peer.GetName().Contains("条未读")
                && dataPeer.GetPattern(PatternInterface.SelectionItem) is not null,
                "cleared unread state preserves native list selection semantics");
            var frozen = new RoleAvatarCard(new("frozen", "审核员", IsFrozen: true));
            frozen.SetStatus(new("frozen", "idle", "待命", 2));
            Check(AutomationProperties.GetName(frozen).Contains("已冻结")
                && AutomationProperties.GetName(frozen).Contains("2 条未读"),
                "frozen role retains unread information");
        }
        finally { control.SetActive(true); }
    }
}
