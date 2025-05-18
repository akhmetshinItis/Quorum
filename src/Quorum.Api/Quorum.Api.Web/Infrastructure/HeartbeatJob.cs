using Quartz;
using Quorum.Web.Services;

namespace Quorum.Web.Infrastructure;

public class HeartbeatJob(RaftService raftService) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        if (!raftService.IsLeader)
            return;

        // Always send heartbeat to followers
        bool heartbeatSuccess = false;
        try
        {
            heartbeatSuccess = await raftService.SendHeartbeat();
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
        }

        // Only commit new entries when heartbeat succeeded and there are uncommitted logs
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
                Console.WriteLine("Quorum is failed");
                raftService.DeleteLastCommittedLog();
            }
        }
    }
}