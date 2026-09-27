using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using PuddingChat.WinUI;

namespace PuddingChat.WinUITests;

public partial class App
{
    private async Task VerifyFileTransferAsync(ChatWorkspace control, Fixture fixture)
    {
        var path = Path.ChangeExtension(Report, ".cs");
        var invalidPath = Path.ChangeExtension(Report, ".bin");
        await File.WriteAllTextAsync(path, "class DroppedFile { }");
        await File.WriteAllBytesAsync(invalidPath, [0, 1, 2]);
        var file = await StorageFile.GetFileFromPathAsync(path);
        var invalid = await StorageFile.GetFileFromPathAsync(invalidPath);
        var image = await StorageFile.GetFileFromPathAsync(Path.ChangeExtension(Report, ".png"));
        await control.SelectRoleAsync("test", fixture.Builder);
        var before = control.Composer.FileCount;
        control.Composer.Draft = "检查附件代码";
        var mixed = new DataPackage(); mixed.SetStorageItems([file, image]);
        await control.Composer.ReceiveAttachmentsAsync(mixed.GetView());
        Check(control.Composer.FileCount == before + 1 && control.Composer.ImageCount > 0 && control.Composer.Draft == "检查附件代码",
            "mixed file transfer imports text and image while preserving draft");
        var imports = fixture.ImageImports;
        var bad = new DataPackage(); bad.SetStorageItems([image, invalid]);
        await control.Composer.ReceiveAttachmentsAsync(bad.GetView());
        Check(control.Composer.FileCount == before + 1 && fixture.ImageImports == imports && control.Composer.TransferError.Length > 0,
            "invalid mixed batch fails before Core image import and preserves draft attachments");

        DataProviderRequest? request = null; DataProviderDeferral? deferral = null;
        var delayed = new DataPackage();
        delayed.SetDataProvider(StandardDataFormats.StorageItems, value => { request = value; deferral = value.GetDeferral(); });
        var pending = control.Composer.ReceiveAttachmentsAsync(delayed.GetView());
        await UntilAsync(() => request is not null);
        await control.SelectRoleAsync("test", fixture.Reviewer);
        var reviewerCount = control.Composer.FileCount;
        try { request!.SetData(new IStorageItem[] { file }); }
        finally { deferral!.Complete(); }
        await pending;
        Check(control.Composer.FileCount == reviewerCount, "delayed file provider does not attach to newly selected role");
        await control.SelectRoleAsync("test", fixture.Builder);
        Check(control.Composer.FileCount == before + 2, "delayed file transfer commits to original role draft");
        var folder = new DataPackage(); folder.SetStorageItems([await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(path)!)]);
        await control.Composer.ReceiveAttachmentsAsync(folder.GetView());
        Check(control.Composer.FileCount == before + 2 && control.Composer.TransferError.Contains("文件夹"),
            "folder transfer is rejected without recursive reads");
        var composer = new ChatComposer(); var called = false;
        composer.ImportFilesAsync = _ => { called = true; return Task.CompletedTask; };
        composer.SetFileAvailability(false);
        await composer.ReceiveAttachmentsAsync(mixed.GetView());
        Check(!called, "disabled file intake does not resolve or import a transfer");
    }
}
