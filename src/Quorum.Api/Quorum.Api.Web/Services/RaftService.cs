using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
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
    public int _lastCommittedIndex = 0;
    private readonly TcpClient _tcpClient;

    public RaftService(IOptions<RaftOptions> options, HttpClient httpClient)
    {
        _httpClient = httpClient;
        var configuration = options.Value;
        _raftNode = new RaftNode(
            configuration.Id, 
            configuration.IsLeader ? NodeState.Leader : NodeState.Follower,
            configuration.Peers);
        _tcpClient = new TcpClient();
    }

    public async Task Append(string command)
    {
        var result = _raftNode.AppendLog(new LogEntry(_logId++, command));
        if (result.Code == Code.RedirectToLeader)
            await _httpClient.PostAsync($"http://localhost:{5000 + result.LeaderId}/api/append?command={command}", new StringContent(""));
    }

    public async Task<List<LogEntry>> GetEntries(int index)
        => await Task.FromResult(_raftNode.Log.Skip(index + 1).ToList());

    public async Task<bool> SendCommit()
    {
        if (_raftNode.Log.Count > 0)
        {
            var count = 1;
            foreach (var follower in _raftNode.Peers!)
            {
                try
                {
                    var result = await _httpClient.PostAsync(
                        $"http://localhost:{5000 + follower}/api/receive", 
                        JsonContent.Create(new List<LogEntry> { _raftNode.Log[^1] }));
                    count += result.IsSuccessStatusCode ? 1 : 0;
                }
                catch
                {
                    continue;
                }
            }
            return count >= 3; 
        }
        return true;
    }

    public async Task<bool> SendHeartbeat()
    {
        var count = 0;
        if (_raftNode.Log.Count > 0)
        {
            foreach (var follower in _raftNode.Peers)
            {
                HttpResponseMessage result = new HttpResponseMessage();
                result.StatusCode = HttpStatusCode.BadRequest;
                try
                {
                    result = await _httpClient.PostAsync($"http://localhost:{5000 + follower}/api/heartbeat",
                        JsonContent.Create(new List<LogEntry> { _raftNode.Log[^1] }));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error sending heartbeat to follower {follower}: {ex.Message}");
                }
                count += (bool)result?.IsSuccessStatusCode ? 1 : 0;
            }
            return count > 1;
        }
        return false;
    }
    
    public async Task ReceiveLogs(List<LogEntry> log)
    {
        if (log.Count > 1)
            _raftNode.Log.AddRange(log);

        if (_raftNode.Log.Count == 0)
        {
            if (log[0].Id == 0)
                _raftNode.Log.Add(log[0]);

            else
            {
                var json = await _httpClient
                    .GetAsync($"http://localhost:{5000 + _raftNode.LeaderId}/api/entries?index=-1").Result.Content
                    .ReadAsStringAsync();
                _raftNode.Log.AddRange(Newtonsoft.Json.JsonConvert.DeserializeObject<List<LogEntry>>(json)!);
            }
        }
        
        else
        {
            if (_raftNode.Log.Count > 0 && log[0].Id != _raftNode.Log[^1].Id + 1)
            {
                var json = await _httpClient
                    .GetAsync($"http://localhost:{5000 + _raftNode.LeaderId}/api/entries?index={_raftNode.Log[^1].Id}").Result.Content
                    .ReadAsStringAsync();
                _raftNode.Log.AddRange(Newtonsoft.Json.JsonConvert.DeserializeObject<List<LogEntry>>(json)!);
            }
            else
                _raftNode.Log.Add(log[0]);
        }
    }

    public void DeleteLastCommittedLog()
    {
        _raftNode.Log = _raftNode.Log.Take(_lastCommittedIndex + 1).ToList();
    }

    public int GetId()
        => _raftNode.Id;

    public async Task<int> GetLeaderId()
    {
        for (int i = 1; i <= 5; i++)
        {
            if (i != _raftNode.Id)
            {
                var isAlive = await IsAlive(i);
                if (isAlive)
                {
                    var response = await _httpClient.GetAsync($"http://localhost:{5000 + i}/api/isLeader");
                    if (response.IsSuccessStatusCode && (await response.Content.ReadAsStringAsync()).Equals("true"))
                        return i;
                }
                
            }
        }
        if (_raftNode.State == NodeState.Leader) return _raftNode.Id;

        return -1;
    }

    public async Task<bool> IsLeader()
        => await Task.FromResult(_raftNode.State == NodeState.Leader);

    public void SetLeader(int id)
    {
        _raftNode.State = NodeState.Follower;
        _raftNode.LeaderId = id;
    }

    public void BecomeLeader()
        => _raftNode.State = NodeState.Leader;

    public async Task<bool> IsAlive(int id)
    {
        try
        {
            using var tcpClient = new TcpClient();
            await tcpClient.ConnectAsync("127.0.0.1", 5000 + id);
        }
        catch (SocketException ex)
        {
            return false;
        }
        return true;
    }
} 