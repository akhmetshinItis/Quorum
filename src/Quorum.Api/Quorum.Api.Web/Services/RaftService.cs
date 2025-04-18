﻿using System.Net;
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

    public RaftService(IOptions<RaftOptions> options, HttpClient httpClient)
    {
        _httpClient = httpClient;
        var configuration = options.Value;
        _raftNode = new RaftNode(
            configuration.Id, 
            configuration.IsLeader ? NodeState.Leader : NodeState.Follower,
            configuration.Followers);
    }

    public async Task Append(string command)
    {
        var result = _raftNode.AppendLog(new LogEntry(_logId++, command));
        if (result.Code == Code.RedirectToLeader)
            await _httpClient.PostAsync($"http://localhost:{5000 + result.LeaderId}/api/append?command={command}", new StringContent("")); // Для уникального адреса к порту прибавляем Id лидера
    }

    public async Task<List<LogEntry>> GetEntries(int index)
        => await Task.FromResult(_raftNode.Log.Skip(index + 1).ToList());

    public async Task<bool> SendCommit()
    {
        if (_raftNode.Log.Count > 0)
        {
            var count = 0;
            foreach (var follower in _raftNode.Followers)
            {
                var result = await _httpClient.PostAsync($"http://localhost:{5000 + follower}/api/receive", JsonContent.Create(new List<LogEntry>  {_raftNode.Log[^1]}));
                count += result.IsSuccessStatusCode ? 1 : 0;
            }
            return count > 2;
        }
        return true;
    }

    public async Task<bool> SendHeartbeat()
    {
        var count = 0;
        if (_raftNode.Log.Count > 0)
        {
            foreach (var follower in _raftNode.Followers)
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
                    .GetAsync($"http://localhost:{5000 + 1}/api/entries?index=-1").Result.Content
                    .ReadAsStringAsync();
                _raftNode.Log.AddRange(Newtonsoft.Json.JsonConvert.DeserializeObject<List<LogEntry>>(json)!);
            }
        }
        
        else
        {
            if (_raftNode.Log.Count > 0 && log[0].Id != _raftNode.Log[^1].Id + 1)
            {
                var json = await _httpClient
                    .GetAsync($"http://localhost:{5000 + 1}/api/entries?index={_raftNode.Log[^1].Id}").Result.Content
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
} 