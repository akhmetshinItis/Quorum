using Quartz;
using Quorum.Web.Services;

namespace Quorum.Web.Infrastructure;

public class ElectionJob(RaftService raftService) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        var id = raftService.GetId();
        
        if (await raftService.GetLeaderId() == -1)
            for (int i = 1; i <= id; i++)
            {
                if (i > id)
                {
                    if (await raftService.IsAlive(id))
                    {
                        raftService.SetLeader(i);
                        return;
                    }
                }
                raftService.BecomeLeader();
            }
            
    }
}