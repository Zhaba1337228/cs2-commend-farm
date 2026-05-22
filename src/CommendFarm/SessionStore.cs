using System.Collections.Concurrent;
using System.Text.Json;

namespace CommendFarm;

public class SessionData
{
    public string Username { get; set; } = "";
    public string? RefreshToken { get; set; }
    public string? GuardData { get; set; }
    public string? AccountName { get; set; }
    public DateTime? LastCommendedAt { get; set; }
}

public class SessionStore
{
    private readonly ConcurrentDictionary<string, SessionData> _sessions = new();
    private readonly string _filePath;
    private readonly ILogger<SessionStore> _logger;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public SessionStore(string filePath, ILogger<SessionStore> logger)
    {
        _filePath = filePath;
        _logger = logger;
    }

    public SessionData? Get(string username)
        => _sessions.TryGetValue(username, out var s) ? s : null;

    public void Update(string username, Action<SessionData> updater)
    {
        var data = _sessions.GetOrAdd(username, _ => new SessionData { Username = username });
        lock (data)
        {
            updater(data);
        }
        Save();
    }

    public void Set(string username, SessionData data)
    {
        _sessions[username] = data;
        Save();
    }

    public void Load()
    {
        if (!File.Exists(_filePath))
        {
            _logger.LogInformation("No sessions file at {Path}, starting fresh", _filePath);
            return;
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            var list = JsonSerializer.Deserialize<List<SessionData>>(json);
            if (list == null) return;

            foreach (var s in list)
                _sessions[s.Username] = s;

            _logger.LogInformation("Loaded {Count} sessions from {Path}", _sessions.Count, _filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load sessions from {Path}", _filePath);
        }
    }

    public void Save()
    {
        try
        {
            var list = _sessions.Values.ToList();
            var json = JsonSerializer.Serialize(list, JsonOpts);
            File.WriteAllText(_filePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save sessions to {Path}", _filePath);
        }
    }

    public bool IsOnCooldown(string username, int cooldownHours)
    {
        if (_sessions.TryGetValue(username, out var data) && data.LastCommendedAt.HasValue)
        {
            return DateTime.UtcNow < data.LastCommendedAt.Value.AddHours(cooldownHours);
        }
        return false;
    }
}
