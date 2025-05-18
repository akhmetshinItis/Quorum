namespace Quorum.Web.Infrastructure;

public class RaftOptions
{
    public bool IsLeader { get; set; }

    public int Id { get; set; }
    
    public List<int>? Followers { get; set; }
    public List<int> AllClusterNodeIds { get; set; } = new List<int>();
}