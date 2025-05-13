namespace Quorum.Api.Core;

public class RaftNode
{
    public int Id { get; set; }
    
    public StateMachine StateMachine { get; set; }
    
    public List<LogEntry> Log { get; set; }
    
    private NodeState _state;
    public NodeState State
    {
        get => _state;
        set
        {
            if (_state != value)
            {
                _state = value;
                if (_loggingService != null)
                {
                    _loggingService.LogStateChange(Id, value);
                }
            }
        }
    }
    
    public List<int>? Followers { get; set; }

    public int LeaderId { get; set; } = 1;
    
    public int CommitIndex { get; set; }
    
    public int LastApplied { get; set; }

    private int _currentTerm = 0;
    private readonly ILoggingService? _loggingService;

    public int CurrentTerm
    {
        get => _currentTerm;
        set
        {
            if (value > _currentTerm)
            {
                _currentTerm = value;
                State = NodeState.Follower;
            }
        }
    }

    public RaftNode(int id, NodeState state, List<int>? followers = null, ILoggingService? loggingService = null)
    {
        Followers = followers;
        Id = id;
        StateMachine = new StateMachine();
        Log = new List<LogEntry>();
        _state = state;
        _loggingService = loggingService;
    }

    public Result AppendLog(LogEntry log)
    {
        if (State == NodeState.Leader)
        {
            Log.Add(log);
            StateMachine.Apply(log.Command);
            return new Result(Code.Success);
        }
        
        return new Result(Code.RedirectToLeader, LeaderId);
    }
}

public enum NodeState
{
    Leader,
    Follower
}