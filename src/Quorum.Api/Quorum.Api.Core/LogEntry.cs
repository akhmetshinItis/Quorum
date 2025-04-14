namespace Quorum.Api.Core;

public class LogEntry(int id, string command)
{
    public int Id { get; } = id;

    public string Command { get; } = command;
}