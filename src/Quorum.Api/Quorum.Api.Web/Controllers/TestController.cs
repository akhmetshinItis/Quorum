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
        await raftService.SendHeartbeat();
    }

    [HttpPost("receive")]
    public async Task Receive(
        [FromServices] RaftService raftService,
        [FromBody] List<LogEntry> logs)
    {
        raftService.ReceiveLogs(logs);
    }
}