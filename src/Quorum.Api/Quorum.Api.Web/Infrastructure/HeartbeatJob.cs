using Quartz;
using Quorum.Web.Services;

namespace Quorum.Web.Infrastructure;

public class HeartbeatJob(RaftService raftService) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        // Console.WriteLine($"HeartbeatJob executed at: [{DateTime.Now}]");
        await raftService.SendHeartbeat();
    }
}