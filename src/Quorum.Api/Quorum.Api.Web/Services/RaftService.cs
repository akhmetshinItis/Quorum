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

        // Если узел не лидер, то находим текущего лидера и синхронизируемся с ним
        if (_raftNode.State == NodeState.Follower)
        {
            // Сначала определяем лидера, затем получаем коммиты
            _ = DiscoverLeaderAndInitializeAsync();
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
            // Определяем текущий индекс (последний индекс логов)
            int currentIndex = _raftNode.Log.Count > 0 ? _raftNode.Log.Count - 1 : -1;
            _loggingService.LogCommandExecution(_raftNode.Id, $"Запрашиваю все коммиты с индекса {currentIndex} у лидера {_raftNode.LeaderId}", true);
            
            var response = await _httpClient.GetAsync($"http://localhost:{5000 + _raftNode.LeaderId}/api/entries?index={currentIndex}");
            response.EnsureSuccessStatusCode();
            
            var json = await response.Content.ReadAsStringAsync();
            var entries = Newtonsoft.Json.JsonConvert.DeserializeObject<List<LogEntry>>(json);
            
            if (entries != null && entries.Count > 0)
            {
                _loggingService.LogCommandExecution(_raftNode.Id, $"Получено {entries.Count} новых коммитов от лидера", true);
                await ReceiveLogs(entries);
                
                // Применяем все новые команды к локальной машине состояний
                foreach (var entry in entries)
                {
                    _raftNode.StateMachine.Apply(entry.Command);
                }
            }
            else
            {
                _loggingService.LogCommandExecution(_raftNode.Id, "Нет новых коммитов для синхронизации", true);
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogCommandExecution(_raftNode.Id, $"Ошибка при синхронизации с лидером: {ex.Message}", false);
        }
    }

    private async Task DiscoverLeaderAndInitializeAsync()
    {
        _loggingService.LogCommandExecution(_raftNode.Id, "Начинаю поиск лидера и синхронизацию коммитов...", true);
        
        // Небольшая задержка для того, чтобы дать другим узлам возможность стартовать
        await Task.Delay(TimeSpan.FromSeconds(3));
        
        var allNodeIds = new List<int>();
        if (_raftNode.Followers != null)
        {
            allNodeIds.AddRange(_raftNode.Followers);
        }
        
        // Добавляем текущий узел и сортируем для приоритета
        allNodeIds.Add(_raftNode.Id);
        allNodeIds = allNodeIds.Distinct().OrderBy(id => id).ToList();

        int? discoveredLeaderId = null;
        
        // Пробуем найти лидера с помощью API запроса к каждому узлу
        foreach (var nodeId in allNodeIds.Where(id => id != _raftNode.Id))
        {
            try
            {
                _loggingService.LogCommandExecution(_raftNode.Id, $"Проверяю, является ли узел {nodeId} лидером", true);
                var leaderResponse = await _httpClient.GetAsync($"http://localhost:{5000 + nodeId}/api/leader");
                if (leaderResponse.IsSuccessStatusCode)
                {
                    var leaderIdJson = await leaderResponse.Content.ReadAsStringAsync();
                    var leaderId = Newtonsoft.Json.JsonConvert.DeserializeObject<int?>(leaderIdJson);
                    
                    if (leaderId.HasValue)
                    {
                        _loggingService.LogCommandExecution(_raftNode.Id, $"Узел {nodeId} сообщает, что лидер - узел {leaderId}", true);
                        discoveredLeaderId = leaderId;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                _loggingService.LogCommandExecution(_raftNode.Id, $"Ошибка при проверке узла {nodeId}: {ex.Message}", false);
            }
        }

        // Если лидер не найден, используем id с наименьшим значением как лидера (по умолчанию)
        if (!discoveredLeaderId.HasValue)
        {
            var activeNodes = await GetActiveNodes();
            if (activeNodes.Count > 0)
            {
                discoveredLeaderId = activeNodes.Min();
                _loggingService.LogCommandExecution(_raftNode.Id, $"Лидер не найден явно, определяю лидера как узел с наименьшим ID: {discoveredLeaderId}", true);
            }
            else
            {
                _loggingService.LogCommandExecution(_raftNode.Id, "Не удалось найти ни одного активного узла, оставляю текущего лидера", false);
                return;
            }
        }

        if (discoveredLeaderId.HasValue)
        {
            _raftNode.LeaderId = discoveredLeaderId.Value;
            _loggingService.LogCommandExecution(_raftNode.Id, $"Установлен лидер: {_raftNode.LeaderId}. Начинаю синхронизацию коммитов...", true);
            await InitializeFollowerAsync();
        }
    }

    private async Task<List<int>> GetActiveNodes()
    {
        var allNodeIds = new List<int>();
        if (_raftNode.Followers != null)
        {
            allNodeIds.AddRange(_raftNode.Followers);
        }
        allNodeIds.Add(_raftNode.Id);
        allNodeIds = allNodeIds.Distinct().OrderBy(id => id).ToList();

        var activeNodes = new List<int>();
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
            }
            catch (Exception)
            {
                // Узел недоступен, пропускаем
            }
        }
        
        return activeNodes;
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
            var latestLogEntry = _raftNode.Log[^1];
            
            // Проверка и логирование последней записи
            _loggingService.LogCommandExecution(_raftNode.Id, 
                $"Отправка последнего коммита подписчикам: ID={latestLogEntry.Id}, Command={(latestLogEntry.Command ?? "null")}", true);
            
            foreach (var follower in _raftNode.Followers)
            {
                try
                {
                    // Используем Newtonsoft.Json для сериализации
                    var json = Newtonsoft.Json.JsonConvert.SerializeObject(new List<LogEntry> { latestLogEntry });
                    var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                    
                    var result = await _httpClient.PostAsync(
                        $"http://localhost:{5000 + follower}/api/receive", content);
                        
                    if (result.IsSuccessStatusCode)
                    {
                        count++;
                        _loggingService.LogCommandExecution(_raftNode.Id, $"Успешно отправлен коммит узлу {follower}", true);
                    }
                    else
                    {
                        _loggingService.LogCommandExecution(_raftNode.Id, 
                            $"Ошибка при отправке коммита узлу {follower}: {result.StatusCode}", false);
                    }
                }
                catch (Exception ex)
                {
                    _loggingService.LogCommandExecution(_raftNode.Id, 
                        $"Исключение при отправке коммита узлу {follower}: {ex.Message}", false);
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
        if (log != null && log.Count > 0)
        {
            _loggingService.LogCommandExecution(_raftNode.Id, $"Получено {log.Count} записей в логе. Первая запись: ID={log[0].Id}, Command={(log[0].Command ?? "null")}", true);
            
            // Фильтруем только новые записи логов (те, которых еще нет)
            var existingLogIds = _raftNode.Log.Select(l => l.Id).ToHashSet();
            var newLogs = log.Where(l => !existingLogIds.Contains(l.Id)).ToList();
            
            if (newLogs.Count > 0)
            {
                _loggingService.LogCommandExecution(_raftNode.Id, $"Добавление {newLogs.Count} новых записей в лог", true);
                _raftNode.Log.AddRange(newLogs);
                
                // Update _logId to be higher than any received log
                _logId = Math.Max(_logId, newLogs.Max(l => l.Id) + 1);
                
                // Применяем новые команды к машине состояний
                foreach (var entry in newLogs)
                {
                    if (!string.IsNullOrEmpty(entry.Command))
                    {
                        _raftNode.StateMachine.Apply(entry.Command);
                    }
                    else
                    {
                        _loggingService.LogCommandExecution(_raftNode.Id, $"Пропуск применения команды для записи ID={entry.Id}, команда null или пустая", false);
                    }
                }
            }
            else
            {
                _loggingService.LogCommandExecution(_raftNode.Id, "Все полученные записи уже присутствуют в логе", true);
            }
        }
        else
        {
            _loggingService.LogCommandExecution(_raftNode.Id, "Получен пустой лог или null", false);
        }

        // Остальная логика обработки только если log не null
        if (log != null && log.Count > 0)
        {
            if (_raftNode.Log.Count == 0)
            {
                if (log[0].Id == 0)
                {
                    _raftNode.Log.Add(log[0]);
                    if (!string.IsNullOrEmpty(log[0].Command))
                    {
                        _loggingService.LogCommandExecution(_raftNode.Id, log[0].Command, true);
                        _raftNode.StateMachine.Apply(log[0].Command);
                    }
                    else
                    {
                        _loggingService.LogCommandExecution(_raftNode.Id, "Добавлена запись ID=0 с пустой командой", false);
                    }
                    _logId = 1; // Next log should have ID 1
                }
                else
                {
                    try
                    {
                        // Fetch full log from current leader
                        var response = await _httpClient.GetAsync($"http://localhost:{5000 + _raftNode.LeaderId}/api/entries?index=-1");
                        response.EnsureSuccessStatusCode();
                        var json = await response.Content.ReadAsStringAsync();
                        var entries = Newtonsoft.Json.JsonConvert.DeserializeObject<List<LogEntry>>(json);
                        
                        if (entries != null && entries.Count > 0)
                        {
                            _raftNode.Log.AddRange(entries);
                            foreach (var entry in entries)
                            {
                                if (!string.IsNullOrEmpty(entry.Command))
                                {
                                    _loggingService.LogCommandExecution(_raftNode.Id, entry.Command, true);
                                    _raftNode.StateMachine.Apply(entry.Command);
                                }
                                else
                                {
                                    _loggingService.LogCommandExecution(_raftNode.Id, $"Пропуск применения команды для записи ID={entry.Id}, команда null или пустая", false);
                                }
                            }
                            // Update _logId based on received entries
                            _logId = Math.Max(_logId, entries.Max(e => e.Id) + 1);
                        }
                    }
                    catch (Exception ex)
                    {
                        _loggingService.LogCommandExecution(_raftNode.Id, $"Ошибка при получении полного лога от лидера: {ex.Message}", false);
                    }
                }
            }
            else
            {
                if (_raftNode.Log.Count > 0 && log[0].Id != _raftNode.Log[^1].Id + 1)
                {
                    try
                    {
                        // Fetch missing entries from current leader
                        var response = await _httpClient.GetAsync($"http://localhost:{5000 + _raftNode.LeaderId}/api/entries?index={_raftNode.Log[^1].Id}");
                        response.EnsureSuccessStatusCode();
                        var json = await response.Content.ReadAsStringAsync();
                        var entries = Newtonsoft.Json.JsonConvert.DeserializeObject<List<LogEntry>>(json);
                        
                        if (entries != null && entries.Count > 0)
                        {
                            _raftNode.Log.AddRange(entries);
                            foreach (var entry in entries)
                            {
                                if (!string.IsNullOrEmpty(entry.Command))
                                {
                                    _loggingService.LogCommandExecution(_raftNode.Id, entry.Command, true);
                                    _raftNode.StateMachine.Apply(entry.Command);
                                }
                                else
                                {
                                    _loggingService.LogCommandExecution(_raftNode.Id, $"Пропуск применения команды для записи ID={entry.Id}, команда null или пустая", false);
                                }
                            }
                            // Update _logId based on received entries
                            _logId = Math.Max(_logId, entries.Max(e => e.Id) + 1);
                        }
                    }
                    catch (Exception ex)
                    {
                        _loggingService.LogCommandExecution(_raftNode.Id, $"Ошибка при получении недостающих записей от лидера: {ex.Message}", false);
                    }
                }
                else
                {
                    _raftNode.Log.Add(log[0]);
                    if (!string.IsNullOrEmpty(log[0].Command))
                    {
                        _loggingService.LogCommandExecution(_raftNode.Id, log[0].Command, true);
                        _raftNode.StateMachine.Apply(log[0].Command);
                    }
                    else
                    {
                        _loggingService.LogCommandExecution(_raftNode.Id, $"Пропуск применения команды для записи ID={log[0].Id}, команда null или пустая", false);
                    }
                    // Update _logId if necessary
                    _logId = Math.Max(_logId, log[0].Id + 1);
                }
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
        }
    }

    public int? GetLeaderId()
    {
        if (_raftNode.State == NodeState.Leader)
            return _raftNode.Id;
        return _raftNode.LeaderId;
    }
} 