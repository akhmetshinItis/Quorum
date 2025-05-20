namespace Quorum.Api.Core;

using System;
using System.Collections.Generic;

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

    private System.Timers.Timer? _electionTimer;
    private int _votesReceived = 0;
    private readonly object _voteLock = new();

    private int? _votedFor = null;
    public int? VotedFor => _votedFor;

    public int CurrentTerm
    {
        get => _currentTerm;
        set
        {
            if (value > _currentTerm)
            {
                _currentTerm = value;
                State = NodeState.Follower;
                _votedFor = null;
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

    public void ResetElectionTimer()
    {
        if (_electionTimer != null)
        {
            _electionTimer.Stop();
            _electionTimer.Interval = GetRandomElectionTimeout();
            _electionTimer.Start();
            _loggingService?.LogCommandExecution(Id, $"Election timer reset", true);
        }
    }

    public void StartElectionTimer(Action? onTimeout = null)
    {
        _electionTimer = new System.Timers.Timer(GetRandomElectionTimeout());
        _electionTimer.Elapsed += (s, e) =>
        {
            _electionTimer.Stop();
            _loggingService?.LogCommandExecution(Id, $"Election timer elapsed, starting election", true);
            onTimeout?.Invoke();
        };
        _electionTimer.AutoReset = false;
        _electionTimer.Start();
        _loggingService?.LogCommandExecution(Id, $"Election timer started", true);
    }

    private int GetRandomElectionTimeout()
    {
        var rnd = new Random(Guid.NewGuid().GetHashCode());
        return rnd.Next(1500, 3000); // миллисекунды, Raft рекомендует 1500-3000
    }
}

public enum NodeState
{
    Leader,
    Follower,
    Candidate
}