using Microsoft.AspNetCore.Mvc;
using PuddingPlatform.Services;

namespace PuddingPlatform.Controllers;

/// <summary>聊天控制器——通过 Platform 发消息到 Controller 链路。</summary>
public class ChatController : Controller
{
    private readonly PlatformApiClient _api;

    public ChatController(PlatformApiClient api) => _api = api;

    public IActionResult Index() => View();

    [HttpPost]
    public async Task<IActionResult> Send(string channelId, string userExternalId, string messageText, string? workspaceId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(messageText))
        {
            ViewBag.Error = "消息内容不能为空";
            return View("Index");
        }

        var result = await _api.SendMessageAsync(
            channelId ?? "web",
            userExternalId ?? "platform-user",
            messageText,
            workspaceId,
            ct: ct);

        ViewBag.Result = result;
        return View("Index");
    }
}
