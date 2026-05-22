using Microsoft.Extensions.Logging;
using SteamKit2;
using SteamKit2.Authentication;

namespace CommendFarm;

public class AccountChecker
{
    private readonly ILogger<AccountChecker> _logger;

    public AccountChecker(ILogger<AccountChecker> logger)
    {
        _logger = logger;
    }

    public async Task<AccountStatus> CheckAsync(
        string username, string password, CancellationToken ct = default)
    {
        var status = new AccountStatus
        {
            Username = username,
            CheckedAt = DateTime.UtcNow
        };

        var client = new SteamClient();
        var manager = new CallbackManager(client);
        var user = client.GetHandler<SteamUser>()!;

        var tcs = new TaskCompletionSource<EResult>();
        var guardNeeded = false;

        manager.Subscribe<SteamClient.ConnectedCallback>(cb =>
        {
            user.LogOn(new SteamUser.LogOnDetails
            {
                Username = username,
                Password = password,
                ShouldRememberPassword = true,
            });
        });

        manager.Subscribe<SteamUser.LoggedOnCallback>(cb =>
        {
            // In v3, Steam Guard results come as specific EResult values
            if (cb.Result == EResult.AccountLogonDenied)
            {
                guardNeeded = true;
                tcs.TrySetResult(cb.Result);
                return;
            }
            tcs.TrySetResult(cb.Result);
        });

        manager.Subscribe<SteamClient.DisconnectedCallback>(cb =>
        {
            if (!tcs.Task.IsCompleted)
                tcs.TrySetResult(EResult.Fail);
        });

        try
        {
            client.Connect();

            var pumpTask = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested && client.IsConnected)
                {
                    manager.RunWaitCallbacks(TimeSpan.FromMilliseconds(100));
                    await Task.Delay(50, ct);
                }
            }, ct);

            var timeout = Task.Delay(TimeSpan.FromSeconds(30), ct);
            var completed = await Task.WhenAny(tcs.Task, timeout);

            if (completed == tcs.Task)
            {
                var result = tcs.Task.Result;
                status.HasSteamGuard = guardNeeded || result == EResult.AccountLogonDenied;
                status.CanLogin = result == EResult.OK || status.HasSteamGuard;
                status.IsBanned = result == EResult.AccountDisabled ||
                                  result == EResult.Banned;
            }
            else
            {
                status.CanLogin = false;
                status.Error = "Timeout";
            }

            client.Disconnect();
        }
        catch (Exception ex)
        {
            status.CanLogin = false;
            status.Error = ex.Message;
        }

        return status;
    }
}

public class AccountStatus
{
    public string Username { get; set; } = "";
    public bool CanLogin { get; set; }
    public bool HasSteamGuard { get; set; }
    public bool IsBanned { get; set; }
    public string? Error { get; set; }
    public DateTime CheckedAt { get; set; }

    public string StatusText => IsBanned ? "BANNED" :
        HasSteamGuard ? "GUARD" :
        CanLogin ? "OK" : "FAIL";
}
