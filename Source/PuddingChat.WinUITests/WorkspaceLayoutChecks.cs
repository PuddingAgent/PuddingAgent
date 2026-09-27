using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PuddingChat.WinUI;

namespace PuddingChat.WinUITests;

public partial class App
{
    private static async Task VerifyWorkspaceLayoutAsync(ChatWorkspace control, Fixture fixture)
    {
        var root = (Grid)control.Content;
        var navigation = root.Children.OfType<Grid>().Single(g => Grid.GetColumn(g) == 0);
        var roles = navigation.Children.OfType<ListView>().Single();
        var chat = root.Children.OfType<Grid>().Single(g => Grid.GetColumn(g) == 1);
        var viewport = chat.Children.OfType<Grid>().Single(g => Grid.GetRow(g) == 2);
        var menu = Descendants<Button>(chat).Single(b => b.Content?.ToString() == "角色");
        var flyout = (Flyout)menu.Flyout;
        var draft = "窄窗口中继续编码";
        control.Composer.Draft = draft;
        try
        {
            control.Width = 320; control.UpdateLayout();
            await UntilAsync(() => menu.Visibility == Visibility.Visible); control.UpdateLayout();
            flyout = (Flyout)menu.Flyout;
            Check(root.ColumnDefinitions[0].ActualWidth == 0 && control.Composer.ActualWidth >= 290 && viewport.ActualHeight > 60,
                "320 DIP workspace reserves usable chat and composer space by moving navigation out of columns");
            flyout.ShowAt(menu);
            await UntilAsync(() => navigation.IsLoaded && VisualTreeHelper.GetOpenPopupsForXamlRoot(control.XamlRoot).Count > 0);
            Check(ReferenceEquals(flyout.Content, navigation) && roles.Items.Count == 2 && navigation.ActualWidth <= 272,
                "compact native flyout hosts the same role controls within narrow width");
            flyout.Hide();
            control.Width = 1050; control.UpdateLayout();
            await UntilAsync(() => menu.Visibility == Visibility.Collapsed); control.UpdateLayout();
            Check(root.Children.Contains(navigation) && flyout.Content is null && root.ColumnDefinitions[0].ActualWidth == 248
                && control.Composer.Draft == draft, "wide layout restores same navigation without resetting role draft");
            control.SetNavigationWidth(0); control.UpdateLayout();
            Check(menu.Visibility == Visibility.Visible && root.ColumnDefinitions[0].ActualWidth == 0,
                "host collapsed navigation still exposes a role switch entry");
            flyout = (Flyout)menu.Flyout;
            var opened = false; var closed = false;
            flyout.Opened += (_, _) => opened = true;
            flyout.Closed += (_, _) => closed = true;
            flyout.ShowAt(menu); await UntilAsync(() => opened); closed = false;
            var next = roles.Items.Cast<RoleAvatarCard>().First(c => !ReferenceEquals(c, roles.SelectedItem));
            roles.SelectedItem = next;
            await UntilAsync(() => control.SelectedRole?.AgentId == next.Agent.AgentId
                && closed);
            Check(control.SelectedRole?.AgentId == next.Agent.AgentId, "selecting role from flyout closes overlay and changes conversation");
            opened = false; closed = false;
            flyout.ShowAt(menu); await UntilAsync(() => opened);
            control.SetActive(false);
            await UntilAsync(() => closed);
            Check(VisualTreeHelper.GetOpenPopupsForXamlRoot(control.XamlRoot).Count == 0, "leaving workbench closes the role overlay");
            control.SetActive(true);
            control.SetNavigationWidth(300); control.UpdateLayout();
            Check(root.ColumnDefinitions[0].ActualWidth == 300 && root.Children.Contains(navigation),
                "host navigation preference survives compact mode");
        }
        finally { flyout.Hide(); control.SetActive(true); control.Width = double.NaN; control.SetNavigationWidth(248); control.UpdateLayout(); }
    }
}
