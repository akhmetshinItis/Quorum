using Quartz;
using Quorum.Web.Services;

namespace Quorum.Web.Infrastructure;

public class ElectionJob(RaftService raftService) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        var id = raftService.GetId();
        var leaderId = await raftService.GetLeaderId();

        if (leaderId == -1)
        {
            for (int i = 1; i <= id; i++)
            {
                if (i < id)
                {
                    if (await raftService.IsAlive(i))
                    {
                        raftService.SetLeader(i);
                        return;
                    }
                }
            }
            raftService.BecomeLeader();
            return;
        }
        if (await raftService.IsAlive(leaderId))
            raftService.SetLeader(leaderId);
    }
}