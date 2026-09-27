using Microsoft.UI.Xaml.Controls;
using PuddingChat.WinUI;
using Windows.System;

namespace PuddingChat.WinUITests;

public partial class App
{
    private static async Task VerifyComposerKeyboardAsync(Grid root)
    {
        var composer = new ChatComposer { Draft = "正在输入中文" };
        composer.SetContext("编码助手", true);
        composer.SetAvailability(true, false, false);
        var sends = 0;
        composer.SendRequested += (_, _) => sends++;
        root.Children.Add(composer);
        try
        {
            await UntilAsync(() => composer.IsLoaded);
            Check(!composer.HandleSubmitKey(VirtualKey.Enter, false, false, false, false)
                && !composer.HandleSubmitKey(VirtualKey.Enter, true, true, false, false)
                && !composer.HandleSubmitKey(VirtualKey.Enter, true, false, true, false) && sends == 0,
                "plain Enter and modified variants retain native editing behavior");
            composer.SetCompositionState(true);
            Check(!composer.HandleSubmitKey(VirtualKey.Enter, true, false, false, false) && sends == 0,
                "IME composition blocks submit shortcut");
            composer.SetCompositionState(false);
            Check(composer.HandleSubmitKey(VirtualKey.Enter, true, false, false, false)
                && composer.HandleSubmitKey(VirtualKey.Enter, true, false, false, true)
                && sends == 1 && composer.Draft == "正在输入中文",
                "Ctrl Enter requests one send and consumes auto-repeat without clearing draft");
            composer.SetAvailability(false, false, false);
            Check(composer.HandleSubmitKey(VirtualKey.Enter, true, false, false, false)
                && composer.HandleSubmitKey(VirtualKey.Enter, true, false, false, true) && sends == 1,
                "disabled send consumes shortcut and repeat without submitting or inserting newlines");
            composer.SetAvailability(true, false, false); composer.SetContext("编码助手", false);
            Check(!composer.HandleSubmitKey(VirtualKey.Enter, true, false, false, false) && sends == 1,
                "disabled editor cannot submit stale draft");
        }
        finally { root.Children.Remove(composer); }
    }
}
