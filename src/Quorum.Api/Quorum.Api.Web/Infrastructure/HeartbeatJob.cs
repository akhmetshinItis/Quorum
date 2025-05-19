using Quartz;
using Quorum.Web.Services;

namespace Quorum.Web.Infrastructure;

public class HeartbeatJob(RaftService raftService) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        bool res = false;
        try
        {
            res = await raftService.SendHeartbeat();
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
        }
        finally
        {

            if (res)
            {
                await raftService.SendCommit();
                raftService.LastCommittedIndex++;
            }
            else
            {
                Console.WriteLine("Quorum is failed");
                raftService.DeleteLastCommittedLog();
            }
        }
    }
}