using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Quorum.Api.Core;
using Quorum.Web.Infrastructure;

namespace Quorum.Web.Services;

public class RaftService
{
    private readonly HttpClient _httpClient;
    private readonly RaftNode _raftNode;
    private int _logId;

    public RaftService(IOptions<RaftOptions> options, HttpClient httpClient)
    {
        _httpClient = httpClient;
        var configuration = options.Value;
        _raftNode = new RaftNode(
            configuration.Id, 
            configuration.IsLeader ? NodeState.Leader : NodeState.Follower);
    }

    public async Task Append(string command)
    {
        var result = _raftNode.AppendLog(new LogEntry(_logId++, command));
        if (result.Code == Code.RedirectToLeader)
            await _httpClient.PostAsync($"http://localhost:{5000 + result.LeaderId}/api/append?command={command}", new StringContent("")); // Для уникального адреса к порту прибавляем Id лидера
    }

    public async Task<List<LogEntry>> GetEntries(int index)
        => await Task.FromResult(_raftNode.Log.Skip(index + 1).ToList());

    public async Task SendHeartbeat()
    {
        await _httpClient.PostAsync($"http://localhost:{5000 + 2}/api/receive", JsonContent.Create(new List<LogEntry> { _raftNode.Log[^1] }));
    }
    
    public async Task ReceiveLogs(List<LogEntry> log)
    {
        if (log.Count > 1)
            _raftNode.Log = log;

        else
        {
            if (_raftNode.Log.Count > 0 && log[0].Id - 1 == _raftNode.Log[^1].Id)
                _raftNode.Log = JsonSerializer.Deserialize<List<LogEntry>>(await _httpClient.GetAsync($"http://localhost:{5000 + 1}/api/entries?index={_raftNode.Log[^1].Id}").Result.Content.ReadAsStringAsync())!;
            else
                _raftNode.Log.Add(log[0]);
        }
    }
}