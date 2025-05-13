namespace Quorum.Api.Core;

public interface ILoggingService
{
    void LogStateChange(int nodeId, NodeState newState);
    void LogCommandExecution(int nodeId, string command, bool success);
    void LogQuorumStatus(int nodeId, bool quorumAchieved);
    void LogNodeInfo(int nodeId, NodeState state, List<int>? followers);
} 