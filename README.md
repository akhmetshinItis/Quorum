# Quorum

### Дизайн-документ: Кворум на ASP.NET с Raft (узлы = ASP.NET приложения, хранилище = in-memory словари)  

#### Функциональные требования:  
- Каждый узел — ASP.NET приложение с HTTP API.  
- Данные хранятся в памяти (`ConcurrentDictionary`).  
- Raft для согласованности: выборы лидера, репликация лога, кворум.  
- Чтение/запись только через лидера (запись требует подтверждения от >N/2 узлов).  
- Heartbeat для поддержания лидерства.  
- Логгирование операций и статусов узлов.  

#### Реализация:  
1. Узел (ASP.NET приложение):  
   - Состояние: Leader, Follower, Candidate.  
   - Данные: ConcurrentDictionary<string, string> (key-value хранилище).  
   - Лог операций: List<LogEntry> (для репликации).  

2. RPC-вызовы (HTTP/gRPC):  
   - RequestVote — для выборов.  
   - AppendEntries — репликация данных + heartbeat.  

3. API для клиентов:  
   - POST /write (ключ, значение) → запись через лидера.  
   - GET /read?key=abc → чтение с проверкой кворума.  
   - GET /status → текущая роль узла (Leader/Follower).  

4. Фоновые процессы:  
   - Таймер выборов (переход в Candidate при отсутствии heartbeat).  
   - Таймер heartbeat (лидер шлет пустые `AppendEntries`).  

#### Пример структуры узла:  
public class RaftNode {
    public Guid NodeId { get; }
    public NodeState State { get; set; } // Leader/Follower/Candidate
    public int CurrentTerm { get; set; }
    public ConcurrentDictionary<string, string> Data { get; } // In-memory "БД"
    public List<LogEntry> Log { get; } // Для репликации
}
#### Отказоустойчивость:  
- При падении лидера — автоматические перевыборы.  
- При потере кворума — запись недоступна, чтение возможно из актуальных узлов.  

#### Логгирование:  
- В консоль + файл (Serilog).  
- Визуальное отображение (/view)
- Панель для управления узлами (Добавление, включение/выключение) (/admin)  

#### Прочее:
- Возможно использование других концепций системного дизайна для стабильности работы
