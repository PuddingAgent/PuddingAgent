using Microsoft.AspNetCore.Mvc;
using PuddingGateway;

namespace PuddingController.Controllers;

/// <summary>Gateway 适配器状态 API。</summary>
[ApiController]
[Route("api/[controller]")]
public class GatewayController : ControllerBase
{
    private readonly GatewayAdapterHost _host;

    public GatewayController(GatewayAdapterHost host)
    {
        _host = host;
    }

    [HttpGet("adapters")]
    public ActionResult GetAdapters()
    {
        return Ok(_host.GetAdapterInfos());
    }
}
