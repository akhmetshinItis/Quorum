using Quartz;
using Quorum.Web.Services;

namespace Quorum.Web.Infrastructure;

public class HeartbeatJob(RaftService raftService) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        if (!raftService.IsLeader)
            return;

        bool heartbeatSuccess = false;
        try
        {
            heartbeatSuccess = await raftService.SendHeartbeat();
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
        }

        if (heartbeatSuccess && raftService.LogCount > raftService._lastCommittedIndex)
        {
            bool commitSuccess = false;
            try
            {
                commitSuccess = await raftService.SendCommit();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during commit: {ex.Message}");
            }

            if (commitSuccess)
            {
                raftService._lastCommittedIndex++;
            }
            else
            {
                Console.WriteLine("Quorum is failed, retaining uncommitted log for retry");
            }
        }
    }
}