namespace Quorum.Web.Infrastructure;

public class RaftOptions
{
    public bool IsLeader { get; set; }

    public int Id { get; set; }
    
    public List<int>? Peers { get; set; }
}