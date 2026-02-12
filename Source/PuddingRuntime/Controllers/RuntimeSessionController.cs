using Microsoft.AspNetCore.Mvc;
using PuddingRuntime.Models;
using PuddingRuntime.Services;

namespace PuddingRuntime.Controllers;

/// <summary>Runtime 会话管理 API——查询活跃 Agent 会话状态。</summary>
[ApiController]
[Route("api/[controller]")]
public class RuntimeSessionController : ControllerBase
{
    private readonly InMemoryRuntimeSessionStore _store;
    private readonly AgentSessionManager _agentSessionManager;

    public RuntimeSessionController(
        InMemoryRuntimeSessionStore store,
        AgentSessionManager agentSessionManager)
    {
        _store = store;
        _agentSessionManager = agentSessionManager;
    }

    /// <summary>列出所有 Runtime 会话（包含活跃和已终止）。</summary>
    [HttpGet]
    public ActionResult<IReadOnlyList<SessionRuntimeRecord>> List()
        => Ok(_store.GetAll());

    /// <summary>获取指定 Runtime 会话状态。</summary>
    [HttpGet("{sessionId}")]
    public ActionResult<SessionRuntimeRecord> Get(string sessionId)
    {
        var record = _store.Get(sessionId);
        return record is null ? NotFound() : Ok(record);
    }

    /// <summary>强制终止指定 Session 的 Agent 实例。</summary>
    [HttpDelete("{sessionId}")]
    public ActionResult Terminate(string sessionId)
    {
        _agentSessionManager.Terminate(sessionId);
        _store.Terminate(sessionId, "manual-termination");
        return NoContent();
    }

    /// <summary>Runtime 概况——活跃 Agent 数量等。</summary>
    [HttpGet("summary")]
    public ActionResult Summary()
    {
        var all = _store.GetAll();
        var active = all.Count(s => s.IsActive);
        var expired = _store.GetExpired(TimeSpan.FromHours(1));

        return Ok(new
        {
            utcNow = DateTimeOffset.UtcNow,
            totalSessions = all.Count,
            activeSessions = active,
            expiredSessions = expired.Count,
            activeAgents = _agentSessionManager.ListActive().Count
        });
    }
}
