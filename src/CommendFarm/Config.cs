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
    public int MatchId { get; set; } = 8;

    public static AppConfig Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
    }
}