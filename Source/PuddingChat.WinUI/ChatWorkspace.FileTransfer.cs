using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace PuddingChat.WinUI;

public sealed partial class ChatWorkspace
{
    private async Task ImportDroppedFilesAsync(DataPackageView data)
    {
        if (_state.Role is not { } role || _addingImages || _busy || _agent is not { IsEnabled: true, IsFrozen: false }) return;
        // Capture the role before asking an asynchronous clipboard/drag provider for its files.
        _addingImages = true; UpdateComposer();
        try
        {
            var token = _lifetime.Token;
            var items = await data.GetStorageItemsAsync().AsTask(token);
            var imageClient = _client as IImageAttachmentClient;
            if (items.Count > (imageClient?.MaxImagesPerMessage ?? 0) + TextFileContexts.MaxFiles)
                throw new ArgumentException("本次添加的文件数量过多，请分批添加。");
            if (items.Count == 0 || items.Any(i => i is not StorageFile || string.IsNullOrWhiteSpace(i.Path)))
                throw new ArgumentException("请添加本地图片或文本文件；暂不支持文件夹和无本地路径的文件。");
            var imagePaths = items.Where(i => NativeImageTransfer.IsImagePath(i.Path)).Select(i => i.Path).ToArray();
            var textPaths = items.Where(i => !NativeImageTransfer.IsImagePath(i.Path)).Select(i => i.Path).ToArray();
            if (imagePaths.Length > 0 && imageClient is null) throw new InvalidOperationException("当前内核暂不支持图片附件。");
            if (imagePaths.Length + _state.ImagesFor(role).Count > (imageClient?.MaxImagesPerMessage ?? 0))
                throw new ArgumentException("图片数量超过每条消息的上限，请分批添加。");
            if (textPaths.Length + _state.FilesFor(role).Count > TextFileContexts.MaxFiles)
                throw new ArgumentException("每条消息最多添加 8 个文本文件。");
            var files = new List<TextFileContext>();
            foreach (var path in textPaths)
                files.Add(await Task.Run(() => TextFileContexts.ReadAsync(path, token), token));
            var combined = _state.FilesFor(role).Concat(files).ToArray();
            TextFileContexts.Validate(combined);
            TextFileContexts.Compose("", combined);
            var images = new List<AttachedImage>();
            foreach (var path in imagePaths) images.Add(await imageClient!.ImportImageAsync(role, path, token));
            token.ThrowIfCancellationRequested();
            if (_disposed) return;
            // Commit the batch to the captured draft only after every import succeeds.
            _state.AddFiles(role, files);
            foreach (var image in images) _state.AddImage(role, image);
            if (_state.Role == role) { Composer.SetFiles(_state.Files); Composer.SetImages(_state.Images); }
        }
        finally { _addingImages = false; if (!_disposed) UpdateComposer(); }
    }
}
