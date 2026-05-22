namespace CommendFarm;

public enum BotResult
{
    Success,
    LoginFailed,
    Banned,
    GuardNeeded,
    GuardFailed,
    GcTimeout,
    CommendFailed,
    Error
}

public record BotAccount(
    string Username,
    string Password,
    string? Email = null,
    string? EmailPassword = null)
{
    public bool HasEmail => !string.IsNullOrEmpty(Email) && !string.IsNullOrEmpty(EmailPassword);

    public static List<BotAccount> LoadFromFile(string path)
    {
        var accounts = new List<BotAccount>();
        foreach (var line in File.ReadAllLines(path))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
                continue;

            var parts = trimmed.Split(':', 4);
            if (parts.Length >= 2)
            {
                accounts.Add(new BotAccount(
                    parts[0].Trim(),
                    parts[1].Trim(),
                    parts.Length > 2 ? parts[2].Trim() : null,
                    parts.Length > 3 ? parts[3].Trim() : null
                ));
            }
        }
        return accounts;
    }
}