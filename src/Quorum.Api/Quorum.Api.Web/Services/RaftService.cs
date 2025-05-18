using System.Net;
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
    private readonly ILoggingService _loggingService;
    private int _logId;
    public int _lastCommittedIndex = 0;
    public bool IsLeader => _raftNode.State == NodeState.Leader;
    public int LogCount => _raftNode.Log.Count;

    public RaftService(IOptions<RaftOptions> options, HttpClient httpClient, ILoggingService loggingService)
    {
        _httpClient = httpClient;
        var configuration = options.Value;
        _loggingService = loggingService;
        _raftNode = new RaftNode(
            configuration.Id,
            configuration.IsLeader ? NodeState.Leader : NodeState.Follower,
            configuration.Followers,
            _loggingService);
        _loggingService.LogNodeInfo(_raftNode.Id, _raftNode.State, _raftNode.Followers);

        if (_raftNode.State != NodeState.Leader)
        {
            MonitorNodes(); // Start monitoring if not leader
        }
    }

    private void MonitorNodes()
    {
        Task.Run(async () =>
        {
            if (_raftNode.State == NodeState.Follower)
            {
                // Initial delay for follower nodes to allow the leader/other nodes to start up
                _loggingService.LogCommandExecution(_raftNode.Id, "Initial startup as follower, delaying first leader check for 10s.", true);
                await Task.Delay(TimeSpan.FromSeconds(10));
            }

            while (true)
            {
                // Periodic delay before each check
                await Task.Delay(TimeSpan.FromSeconds(5)); 
                await CheckLeaderStatus();
            }
        });
    }

    private async Task CheckLeaderStatus()
    {
        var allNodeIds = new List<int>();
        if (_raftNode.Followers != null)
        {
            allNodeIds.AddRange(_raftNode.Followers);
        }
        allNodeIds.Add(_raftNode.Id);
        allNodeIds = allNodeIds.Distinct().OrderBy(id => id).ToList(); // Order by ID for priority

        var activeNodes = new List<int>();
        // Use a new HttpClient with a short timeout for health checks
        // to avoid long waits if a node is unresponsive.
        using var healthCheckClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };

        foreach (var nodeId in allNodeIds)
        {
            if (nodeId == _raftNode.Id)
            {
                activeNodes.Add(_raftNode.Id); // Current node is active
                continue;
            }
            try
            {
                var response = await healthCheckClient.GetAsync($"http://localhost:{5000 + nodeId}/api/health");
                if (response.IsSuccessStatusCode)
                {
                    activeNodes.Add(nodeId);
                }
                else
                {
                    _loggingService.LogCommandExecution(_raftNode.Id, $"Node {nodeId} health check failed with status {response.StatusCode}.", false);
                }
            }
            catch (Exception ex)
            {
                _loggingService.LogCommandExecution(_raftNode.Id, $"Node {nodeId} is not reachable for health check: {ex.Message}", false);
            }
        }
        
        activeNodes.Sort(); // Ensure sorted by ID for priority

        if (activeNodes.Count > 0)
        {
            int determinedLeaderId = activeNodes[0]; // Lowest ID is the leader
            if (_raftNode.Id == determinedLeaderId)
            {
                if (_raftNode.State != NodeState.Leader)
                {
                    _raftNode.State = NodeState.Leader;
                    // LogStateChange doesn't take a custom message, using LogCommandExecution for context
                    _loggingService.LogCommandExecution(_raftNode.Id, $"Became leader as highest priority. Active nodes: {string.Join(",", activeNodes)}", true);
                }
                _raftNode.LeaderId = _raftNode.Id;
            }
            else
            {
                if (_raftNode.State == NodeState.Leader)
                {
                    _raftNode.State = NodeState.Follower;
                     _loggingService.LogCommandExecution(_raftNode.Id, $"Stepped down. New leader is {determinedLeaderId}. Active nodes: {string.Join(",", activeNodes)}", true);
                }
                 else if (_raftNode.LeaderId != determinedLeaderId && _raftNode.State == NodeState.Follower)
                {
                    _loggingService.LogCommandExecution(_raftNode.Id, $"Detected new leader {determinedLeaderId}. Previous: {_raftNode.LeaderId}. Active: {string.Join(",", activeNodes)}", true);
                }
                _raftNode.LeaderId = determinedLeaderId;
            }
        }
        else
        {
            _loggingService.LogCommandExecution(_raftNode.Id, "No active nodes found (including self by direct check, which is unexpected). Remaining follower.", false);
             if (_raftNode.State == NodeState.Leader) // If was leader but no one is active
            {
                _raftNode.State = NodeState.Follower; // Step down
                _loggingService.LogCommandExecution(_raftNode.Id, "Stepped down as leader, no active nodes found.", true);
            }
        }
    }

    public async Task Append(string command)
    {
        var result = _raftNode.AppendLog(new LogEntry(_logId++, command));
        _loggingService.LogCommandExecution(_raftNode.Id, command, result.Code == Code.Success);
        
        if (result.Code == Code.RedirectToLeader)
            await _httpClient.PostAsync($"http://localhost:{5000 + result.LeaderId}/api/append?command={command}", new StringContent(""));
    }

    public async Task<List<LogEntry>> GetEntries(int index)
    {
        var entries = await Task.FromResult(_raftNode.Log.Skip(index + 1).ToList());
        if (entries.Count > 0) 
            _loggingService.LogCommandExecution(_raftNode.Id, $"Запрос логов начиная с индекса {index}, получено {entries.Count} записей", true);
        return entries;
    }

    public async Task<bool> SendCommit()
    {
        if (_raftNode.Log.Count > 0)
        {
            var count = 1;
            foreach (var follower in _raftNode.Followers)
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
            var quorumAchieved = count >= 3;
            _loggingService.LogQuorumStatus(_raftNode.Id, quorumAchieved);
            return quorumAchieved;
        }
        return true;
    }

    public async Task<bool> SendHeartbeat()
    {
        var count = 0;
        foreach (var follower in _raftNode.Followers)
        {
            HttpResponseMessage result = new HttpResponseMessage();
            result.StatusCode = HttpStatusCode.BadRequest;
            try
            {
                result = await _httpClient.PostAsync($"http://localhost:{5000 + follower}/api/heartbeat?leaderId={_raftNode.Id}&term={_raftNode.CurrentTerm}", null);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending heartbeat to follower {follower}: {ex.Message}");
            }
            count += (bool)result?.IsSuccessStatusCode ? 1 : 0;
        }
        var quorumAchieved = count > 1;
        _loggingService.LogQuorumStatus(_raftNode.Id, quorumAchieved);
        return _raftNode.Followers.Count > 0;
    }
    
    public async Task ReceiveLogs(List<LogEntry> log)
    {
        if (log.Count > 1)
            _raftNode.Log.AddRange(log);

        if (_raftNode.Log.Count == 0)
        {
            if (log[0].Id == 0)
            {
                _raftNode.Log.Add(log[0]);
                _loggingService.LogCommandExecution(_raftNode.Id, log[0].Command, true);
            }
            else
            {
                // Fetch full log from current leader
                var response = await _httpClient.GetAsync($"http://localhost:{5000 + _raftNode.LeaderId}/api/entries?index=-1");
                var json = await response.Content.ReadAsStringAsync();
                var entries = Newtonsoft.Json.JsonConvert.DeserializeObject<List<LogEntry>>(json)!;
                _raftNode.Log.AddRange(entries);
                foreach (var entry in entries)
                {
                    _loggingService.LogCommandExecution(_raftNode.Id, entry.Command, true);
                }
            }
        }
        else
        {
            if (_raftNode.Log.Count > 0 && log[0].Id != _raftNode.Log[^1].Id + 1)
            {
                // Fetch missing entries from current leader
                var response = await _httpClient.GetAsync($"http://localhost:{5000 + _raftNode.LeaderId}/api/entries?index={_raftNode.Log[^1].Id}");
                var json = await response.Content.ReadAsStringAsync();
                var entries = Newtonsoft.Json.JsonConvert.DeserializeObject<List<LogEntry>>(json)!;
                _raftNode.Log.AddRange(entries);
                foreach (var entry in entries)
                {
                    _loggingService.LogCommandExecution(_raftNode.Id, entry.Command, true);
                }
            }
            else
            {
                _raftNode.Log.Add(log[0]);
                _loggingService.LogCommandExecution(_raftNode.Id, log[0].Command, true);
            }
        }
    }

    public void DeleteLastCommittedLog()
    {
        _raftNode.Log = _raftNode.Log.Take(_lastCommittedIndex + 1).ToList();
    }

    public async Task HeartbeatReceived([FromQuery] int? leaderId = null, [FromQuery] int? term = null)
    {
        // Simplified: A follower updates its leader knowledge. Term logic removed for priority system.
        if (_raftNode.State != NodeState.Leader && leaderId.HasValue)
        {
            if (_raftNode.LeaderId != leaderId.Value)
            {
                 _loggingService.LogCommandExecution(_raftNode.Id, $"Heartbeat from {leaderId.Value}. Updating known leader from {_raftNode.LeaderId} to {leaderId.Value}.", true);
                _raftNode.LeaderId = leaderId.Value;
            }
            // In a Raft system, we'd reset an election timer here.
            // In this priority system, periodic CheckLeaderStatus handles leader failure.
            // For more responsive failure detection, followers could have their own timer that,
            // if heartbeats stop, triggers an immediate CheckLeaderStatus.
            // For now, relying on periodic CheckLeaderStatus.
        }
        // Term logic from Raft's HeartbeatReceived is removed to avoid conflicts with priority system
        // if (term.HasValue && term.Value > _raftNode.CurrentTerm) { ... }
    }

    public int? GetLeaderId()
    {
        if (_raftNode.State == NodeState.Leader)
            return _raftNode.Id;
        return _raftNode.LeaderId == 0 ? null : _raftNode.LeaderId;
    }
} 