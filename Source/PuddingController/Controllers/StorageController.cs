using Microsoft.AspNetCore.Mvc;
using PuddingCode.Platform;
using PuddingController.Services;

namespace PuddingController.Controllers;

/// <summary>
/// Workspace 统一存储 API。
/// 职责：对象 Put / Get / Delete / List。
/// 路径：/api/storage/{workspaceId}/...
/// </summary>
[ApiController]
[Route("api/storage/{workspaceId}")]
public sealed class StorageController : ControllerBase
{
    private readonly UnifiedStorageService _storage;
    private readonly InMemoryWorkspaceCatalog _catalog;

    public StorageController(UnifiedStorageService storage, InMemoryWorkspaceCatalog catalog)
    {
        _storage = storage;
        _catalog = catalog;
    }

    /// <summary>列举对象元数据，可选路径前缀过滤。</summary>
    [HttpGet("objects")]
    public ActionResult<IReadOnlyList<StorageObjectMeta>> List(
        string workspaceId, [FromQuery] string? prefix = null)
    {
        if (!WorkspaceExists(workspaceId)) return NotFound();
        return Ok(_storage.List(workspaceId, prefix));
    }

    /// <summary>取对象内容（base64）。</summary>
    [HttpGet("objects/{objectId}")]
    public ActionResult<StorageGetResponse> Get(string workspaceId, string objectId)
    {
        if (!WorkspaceExists(workspaceId)) return NotFound();
        var obj = _storage.Get(workspaceId, objectId);
        return obj is null ? NotFound() : Ok(obj);
    }

    /// <summary>上传对象（base64 内容），幂等。</summary>
    [HttpPut("objects")]
    public ActionResult<StorageObjectMeta> Put(
        string workspaceId, [FromBody] StoragePutRequest req)
    {
        if (!WorkspaceExists(workspaceId)) return NotFound();
        try
        {
            var meta = _storage.Put(workspaceId, req);
            return Ok(meta);
        }
        catch (FormatException)
        {
            return BadRequest("ContentBase64 is not valid base64.");
        }
    }

    /// <summary>删除对象。</summary>
    [HttpDelete("objects/{objectId}")]
    public ActionResult Delete(string workspaceId, string objectId)
    {
        if (!WorkspaceExists(workspaceId)) return NotFound();
        return _storage.Delete(workspaceId, objectId) ? NoContent() : NotFound();
    }

    private bool WorkspaceExists(string workspaceId) =>
        _catalog.GetWorkspace(workspaceId) is not null;
}
