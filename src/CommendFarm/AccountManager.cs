using Microsoft.Extensions.Logging;

namespace CommendFarm;

public class AccountManager
{
    private readonly ILogger<AccountManager> _logger;
    private readonly AppConfig _config;
    private readonly SessionStore _sessionStore;
    private int _successCount;
    private int _failCount;

    public int SuccessCount => _successCount;
    public int FailCount => _failCount;

    public AccountManager(AppConfig config, SessionStore sessionStore, ILogger<AccountManager> logger)
    {
        _config = config;
        _sessionStore = sessionStore;
        _logger = logger;
    }

    public bool IsOnCooldown(string username)
        => _sessionStore.IsOnCooldown(username, _config.CooldownHours);

    public void MarkCommended(string username)
    {
        _sessionStore.Update(username, d => d.LastCommendedAt = DateTime.UtcNow);
        Interlocked.Increment(ref _successCount);
        _logger.LogInformation("Account {User} commended (total success: {Count})", username, _successCount);
    }

    public void MarkFailed(string username)
    {
        Interlocked.Increment(ref _failCount);
        _logger.LogWarning("Account {User} failed (total fails: {Count})", username, _failCount);
    }

    public void ResetCounts()
    {
        Interlocked.Exchange(ref _successCount, 0);
        Interlocked.Exchange(ref _failCount, 0);
    }
}
