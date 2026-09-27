using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;
using PuddingChat.WinUI;

namespace PuddingChat.WinUITests;

public partial class App
{
    private async Task VerifyImageTransferAsync()
    {
        var path = Path.ChangeExtension(Report, ".png");
        var file = await StorageFile.GetFileFromPathAsync(path);
        var text = new DataPackage(); text.SetText("普通文字");
        Check(!NativeImageTransfer.ContainsImages(text.GetView()), "plain text is left to native TextBox paste");
        var files = new DataPackage(); files.SetStorageItems([file]);
        string? imported = null;
        await NativeImageTransfer.ReadAsync(files.GetView(), paths => { imported = paths.Single(); return Task.CompletedTask; }, default);
        Check(imported == path && File.Exists(path), "storage item transfer preserves original source file");
        var bitmap = new DataPackage(); bitmap.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
        string? temporary = null;
        await NativeImageTransfer.ReadAsync(bitmap.GetView(), async paths =>
        {
            temporary = paths.Single();
            var staged = await File.ReadAllBytesAsync(temporary); var original = await File.ReadAllBytesAsync(path);
            Check(staged.SequenceEqual(original), "clipboard bitmap keeps original encoded bytes");
        }, default);
        Check(temporary is not null && !File.Exists(temporary), "clipboard staging file is removed after import");
        try
        {
            await NativeImageTransfer.ReadAsync(bitmap.GetView(), paths => { temporary = paths.Single(); throw new InvalidOperationException("test import failure"); }, default);
            throw new Exception("expected import failure");
        }
        catch (InvalidOperationException e) when (e.Message == "test import failure") { }
        Check(!File.Exists(temporary), "clipboard staging is cleaned when Core import fails");
        var unsupported = Path.ChangeExtension(Report, ".txt"); await File.WriteAllTextAsync(unsupported, "not an image");
        var mixed = new DataPackage(); mixed.SetStorageItems([file, await StorageFile.GetFileFromPathAsync(unsupported)]);
        var called = false;
        try { await NativeImageTransfer.ReadAsync(mixed.GetView(), _ => { called = true; return Task.CompletedTask; }, default); }
        catch (ArgumentException) { }
        Check(!called, "mixed unsupported file drop is rejected before import");
    }
    private async Task VerifyComposerImageTransferAsync(ChatWorkspace control, Fixture fixture)
    {
        var image = await StorageFile.GetFileFromPathAsync(Path.ChangeExtension(Report, ".png"));
        await control.SelectRoleAsync("test", fixture.Builder);
        control.Composer.Draft = "保留输入中的文字";
        DataProviderRequest? request = null; DataProviderDeferral? deferral = null;
        var delayed = new DataPackage();
        delayed.SetDataProvider(StandardDataFormats.Bitmap, value => { request = value; deferral = value.GetDeferral(); });
        var pending = control.Composer.ReceiveImagesAsync(delayed.GetView());
        await UntilAsync(() => request is not null);
        try
        {
            await control.SelectRoleAsync("test", fixture.Reviewer);
            request!.SetData(RandomAccessStreamReference.CreateFromFile(image));
        }
        finally { deferral!.Complete(); }
        await pending;
        Check(control.Composer.ImageCount == 0, "delayed paste cannot attach to newly selected role");
        await control.SelectRoleAsync("test", fixture.Builder);
        Check(control.Composer.ImageCount == 1 && control.Composer.Draft == "保留输入中的文字",
            "paste completes in original role draft without changing text");
        var text = new DataPackage(); text.SetText("普通文字");
        await control.Composer.ReceiveImagesAsync(text.GetView());
        Check(control.Composer.TransferError.Length > 0 && control.Composer.ImageCount == 1, "explicit paste image reports unsupported clipboard without discarding attachments");
        var standalone = new ChatComposer(); var calls = 0;
        standalone.ImportImagesAsync = _ => { calls++; return Task.CompletedTask; };
        standalone.SetAttachmentAvailability(false);
        await standalone.ReceiveImagesAsync(delayed.GetView());
        Check(calls == 0, "disabled composer rejects paste and drop intake");
    }
}
