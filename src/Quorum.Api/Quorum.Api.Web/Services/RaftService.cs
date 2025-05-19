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

        // Initialize _logId based on existing logs to avoid duplicate IDs
        _logId = _raftNode.Log.Count > 0 ? _raftNode.Log.Max(l => l.Id) + 1 : 1;

        if (_raftNode.State == NodeState.Follower)
        {
            // Fetch full log from leader on follower startup
            _ = InitializeFollowerAsync();
            MonitorNodes(); // Start monitoring follower status
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

    private async Task InitializeFollowerAsync()
    {
        try
        {
            var response = await _httpClient.GetAsync($"http://localhost:{5000 + _raftNode.LeaderId}/api/entries?index=-1");
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            var entries = JsonSerializer.Deserialize<List<LogEntry>>(json);
            if (entries != null)
                await ReceiveLogs(entries);
        }
        catch (Exception ex)
        {
            _loggingService.LogCommandExecution(_raftNode.Id, $"Failed to initialize logs from leader: {ex.Message}", false);
        }
    }

    public async Task Append(string command)
    {
        if (_raftNode.State == NodeState.Leader)
        {
            var logEntry = new LogEntry(_logId++, command);
            _raftNode.Log.Add(logEntry);
            // Immediately apply command locally on leader
            _raftNode.StateMachine.Apply(command);
            _loggingService.LogCommandExecution(_raftNode.Id, command, true);
        }
        else
        {
            // Redirect to leader
            await _httpClient.PostAsync(
                $"http://localhost:{5000 + _raftNode.LeaderId}/api/append?command={command}",
                new StringContent(""));
        }
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
            var count = 1; // include leader itself
            foreach (var follower in _raftNode.Followers)
            {
                try
                {
                    var result = await _httpClient.PostAsync(
                        $"http://localhost:{5000 + follower}/api/receive",
                        JsonContent.Create(new List<LogEntry> { _raftNode.Log[^1] }));
                    if (result.IsSuccessStatusCode)
                        count++;
                }
                catch
                {
                    // ignore failed follower
                }
            }
            // fixed quorum size
            const int quorum = 3;
            var quorumAchieved = count >= quorum;
            _loggingService.LogQuorumStatus(_raftNode.Id, quorumAchieved);
            return quorumAchieved;
        }
        return true;
    }

    public async Task<bool> SendHeartbeat()
    {
        var count = 1; // include leader itself
        foreach (var follower in _raftNode.Followers)
        {
            try
            {
                var result = await _httpClient.PostAsync(
                    $"http://localhost:{5000 + follower}/api/heartbeat?leaderId={_raftNode.Id}&term={_raftNode.CurrentTerm}",
                    null);
                if (result.IsSuccessStatusCode)
                    count++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending heartbeat to follower {follower}: {ex.Message}");
            }
        }
        // fixed quorum size
        const int quorum = 3;
        var quorumAchieved = count >= quorum;
        _loggingService.LogQuorumStatus(_raftNode.Id, quorumAchieved);
        return quorumAchieved;
    }
    
    public async Task ReceiveLogs(List<LogEntry> log)
    {
        if (log.Count > 1)
        {
            _raftNode.Log.AddRange(log);
            // Update _logId to be higher than any received log
            if (log.Count > 0)
                _logId = Math.Max(_logId, log.Max(l => l.Id) + 1);
        }

        if (_raftNode.Log.Count == 0)
        {
            if (log[0].Id == 0)
            {
                _raftNode.Log.Add(log[0]);
                _loggingService.LogCommandExecution(_raftNode.Id, log[0].Command, true);
                _logId = 1; // Next log should have ID 1
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
                // Update _logId based on received entries
                if (entries.Count > 0)
                    _logId = Math.Max(_logId, entries.Max(e => e.Id) + 1);
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
                // Update _logId based on received entries
                if (entries.Count > 0)
                    _logId = Math.Max(_logId, entries.Max(e => e.Id) + 1);
            }
            else
            {
                _raftNode.Log.Add(log[0]);
                _loggingService.LogCommandExecution(_raftNode.Id, log[0].Command, true);
                // Update _logId if necessary
                _logId = Math.Max(_logId, log[0].Id + 1);
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
        return _raftNode.LeaderId;
    }
} 