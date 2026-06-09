using System.Text.Json;

namespace CommendFarm;

public class AppConfig
{
    public string TargetSteamId64 { get; set; } = "";
    public bool CommendFriendly { get; set; } = true;
    public bool CommendLeader { get; set; } = true;
    public bool CommendTeacher { get; set; } = true;
    public int CooldownHours { get; set; } = 12;
    public int LoginDelayMs { get; set; } = 5000;
    public int BatchSize { get; set; } = 10;
    public int BatchDelayMs { get; set; } = 30000;
    public string AccountsFile { get; set; } = "accounts.txt";
    public int MatchId { get; set; } = 8; // 8 = dummy (работает без сервера)
    // RCON для реального match_id
    public string? RconHost { get; set; } // пример: "1.2.3.4"
    public int RconPort { get; set; } = 27015;
    public string? RconPassword { get; set; }
    public bool UseRconMatchId { get; set; } = false;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static AppConfig Load(string path)
    {
        if (!File.Exists(path))
            return new AppConfig();

        var json = File.ReadAllText(path);
        var cfg = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
        // Fallback defaults для старых конфигов
        if (cfg.MatchId == 0) cfg.MatchId = 8;
        if (cfg.RconPort == 0) cfg.RconPort = 27015;
        return cfg;
    }

    public void Save(string path)
    {
        var json = JsonSerializer.Serialize(this, JsonOpts);
        File.WriteAllText(path, json);
    }
}