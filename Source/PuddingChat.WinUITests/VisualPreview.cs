using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media.Imaging;
using PuddingChat;
using PuddingChat.WinUI;
using System.Runtime.InteropServices.WindowsRuntime;

namespace PuddingChat.WinUITests;

public partial class App
{
    private async Task<string[]> CaptureVisualPreviewsAsync(Grid root)
    {
        var fixture = new Fixture(Path.ChangeExtension(Report, ".preview-image.png"))
        {
            Sent = PendingSend.Create(new("test", "builder"), "session", "请解释并实现这个计算。"), Terminal = true,
            AssistantContent = "已完成计算函数，可直接在项目中使用。\n\n```csharp\npublic double Square(double x) => x * x;\n```\n\n结果满足 $f(x)=x^2$，例如 $f(3)=9$。",
            AssistantProcess = [new("thought", "thinking", "done", "先确认输入与返回值，再实现纯函数并验证边界。", 1),
                new("tool", "tool_call", "done", "dotnet test", 2, "terminal", Arguments: "dotnet test", Output: "已通过：12；失败：0", ExitCode: 0, ToolCallId: "call", TurnId: "turn")]
        };
        using var view = new ChatWorkspace(fixture);
        var surface = (Border)XamlReader.Load("<Border xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Background=\"{ThemeResource ApplicationPageBackgroundThemeBrush}\" />");
        surface.Child = view; surface.Height = 740; surface.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetColumnSpan(surface, 2); root.Children.Add(surface);
        var paths = new List<string>();
        try
        {
            await view.InitializeAsync(); await view.SelectRoleAsync("test", fixture.Builder);
            view.Composer.Draft = "继续检查边界条件";
            foreach (var (name, width, theme) in new[] { ("wide-light", 1000, ElementTheme.Light), ("compact-light", 360, ElementTheme.Light), ("wide-dark", 1000, ElementTheme.Dark) })
            {
                surface.Width = width; surface.RequestedTheme = theme; root.UpdateLayout();
                await Task.Delay(250); root.UpdateLayout();
                view.ScrollToLatest();
                await NextVisualFrameAsync(); root.UpdateLayout();
                foreach (var math in Descendants<MathFormulaView>(view)) await math.Rendering;
                root.UpdateLayout(); await NextVisualFrameAsync();
                await File.WriteAllTextAsync(Path.ChangeExtension(Report, "." + name + ".math.json"),
                    System.Text.Json.JsonSerializer.Serialize(Descendants<MathFormulaView>(view).Select(math => new { math.Latex, math.IsLoaded, math.Rendered, math.RenderError })));
                var target = new RenderTargetBitmap(); await target.RenderAsync(surface);
                var pixels = (await target.GetPixelsAsync()).ToArray();
                var path = Path.ChangeExtension(Report, "." + name + ".png");
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(Report);
                var folder = await file.GetParentAsync();
                var output = await folder.CreateFileAsync(Path.GetFileName(path), Windows.Storage.CreationCollisionOption.ReplaceExisting);
                using var stream = await output.OpenAsync(Windows.Storage.FileAccessMode.ReadWrite);
                var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
                encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                    (uint)target.PixelWidth, (uint)target.PixelHeight, 96, 96, pixels);
                await encoder.FlushAsync(); paths.Add(path);
            }
        }
        finally { root.Children.Remove(surface); }
        return paths.ToArray();
    }
}
