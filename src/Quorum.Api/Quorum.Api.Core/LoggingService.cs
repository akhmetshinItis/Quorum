using System.Text;

namespace Quorum.Api.Core;

public class LoggingService : ILoggingService
{
    private readonly string _logFilePath;
    private readonly object _lockObject = new();

    public LoggingService(string logFilePath = "quorum.log")
    {
        _logFilePath = logFilePath;
    }

    public void LogStateChange(int nodeId, NodeState newState)
    {
        var message = $"Узел {nodeId}: Изменение состояния на {GetStateInRussian(newState)}";
        WriteLog(message);
    }

    public void LogCommandExecution(int nodeId, string command, bool success)
    {
        var status = success ? "успешно выполнена" : "не выполнена";
        var message = $"Узел {nodeId}: Команда '{command}' {status}";
        WriteLog(message);
    }

    public void LogQuorumStatus(int nodeId, bool quorumAchieved)
    {
        var status = quorumAchieved ? "достигнут" : "не достигнут";
        var message = $"Узел {nodeId}: Кворум {status}";
        WriteLog(message);
    }

    public void LogNodeInfo(int nodeId, NodeState state, List<int>? followers)
    {
        var followersInfo = followers != null && followers.Any() 
            ? $"Подписчики: {string.Join(", ", followers)}" 
            : "Нет подписчиков";
        var message = $"Узел {nodeId}: Роль - {GetStateInRussian(state)}, {followersInfo}";
        WriteLog(message);
    }

    private void WriteLog(string message)
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var logEntry = $"[{timestamp}] {message}";

        lock (_lockObject)
        {
            File.AppendAllText(_logFilePath, logEntry + Environment.NewLine, Encoding.UTF8);
        }
    }

    private static string GetStateInRussian(NodeState state) => state switch
    {
        NodeState.Leader => "Лидер",
        NodeState.Follower => "Подписчик",
        _ => state.ToString()
    };
} 