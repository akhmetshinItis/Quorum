using Microsoft.AspNetCore.Mvc;
using Quorum.Api.Core;
using Quorum.Web.Services;

namespace Quorum.Web.Controllers;

[ApiController]
[Route("api/")]
public class TestController : ControllerBase
{
    [HttpPost("append")]
    public async Task Append(
        [FromServices] RaftService raftService,
        [FromQuery] string command)
    {
        await raftService.Append(command);
    }

    [HttpGet("entries")]
    public async Task<List<LogEntry>> GetEntries(
        [FromServices] RaftService raftService,
        [FromQuery] int index = -1)
    {
        return await raftService.GetEntries(index);
    }

    [Obsolete("Для тестов")]
    [HttpPost("replicate")]
    public async Task Replicate([FromServices] RaftService raftService)
    {
        await raftService.SendCommit();
    }

    [HttpPost("receive")]
    public async Task Receive(
        [FromServices] RaftService raftService,
        [FromBody] List<LogEntry> logs)
    {
        raftService.ReceiveLogs(logs);
    }

    [HttpPost("heartbeat")]
    public async Task<IActionResult> Heartbeat([FromServices] RaftService raftService, [FromQuery] int? leaderId = null, [FromQuery] int? term = null)
    {
        await raftService.HeartbeatReceived(leaderId, term);
        return Ok();
    }

    [HttpGet("leader")]
    public IActionResult GetLeader([FromServices] RaftService raftService)
    {
        return Ok(raftService.GetLeaderId());
    }

    [HttpGet("health")]
    public IActionResult HealthCheck()
    {
        // This endpoint simply indicates that the node is alive and responsive.
        return Ok();
    }
}