using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PuddingChat;
using PuddingChat.WinUI;

namespace PuddingChat.WinUITests;

public partial class App
{
    private static async Task VerifyTextFilesAsync(ChatWorkspace control, Fixture fixture)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".cs");
        var invalid = path + ".bin";
        try
        {
            await File.WriteAllTextAsync(path, "class Example { }\r\n");
            await File.WriteAllBytesAsync(invalid, [0, 1, 2]);
            await control.SelectRoleAsync("test", fixture.Reviewer);
            await control.AddTextFilesAsync([path]);
            Check(control.Composer.FileCount == 1, "native workspace imports a text snapshot into role draft");
            var root = (StackPanel)((Border)control.Composer.Content).Child;
            var rows = (StackPanel)root.Children.OfType<ScrollViewer>().Single().Content;
            var preview = rows.Children.OfType<Grid>().SelectMany(r => r.Children.OfType<Button>()).Single(b => b.Flyout is not null);
            var text = (TextBlock)((ScrollViewer)((Flyout)preview.Flyout).Content).Content;
            Check(text.Text == "class Example { }\r\n", "native attachment preview preserves imported source");
            await File.WriteAllTextAsync(path, "changed");
            Check(text.Text == "class Example { }\r\n", "source edits do not change attachment preview");
            try { await control.AddTextFilesAsync([path, invalid]); throw new Exception("binary import unexpectedly accepted"); }
            catch (ArgumentException) { }
            Check(control.Composer.FileCount == 1, "failed multi-file import leaves draft unchanged");
            var remove = rows.Children.OfType<Grid>().SelectMany(r => r.Children.OfType<Button>()).Single(b => b.Tag is string);
            var peer = new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(remove);
            ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
            await UntilAsync(() => control.Composer.FileCount == 0);
            Check(control.Composer.FileCount == 0, "native removal clears selected file snapshot");
            await control.AddTextFilesAsync([path]);
            await control.SendAsync();
            Check(fixture.Sent?.Files is { Count: 1 } sent && sent[0].Text == "changed"
                && fixture.Sent.SubmittedText.Contains("changed"), "native send forwards the selected snapshot through the client port");
            Check(control.Composer.FileCount == 0, "accepted native send removes submitted file card");
        }
        finally { File.Delete(path); File.Delete(invalid); }
    }
}
