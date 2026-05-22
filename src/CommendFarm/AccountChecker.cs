using Microsoft.Extensions.Logging;
using SteamKit2;

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
        var guardTcs = new TaskCompletionSource<bool>();

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
            tcs.TrySetResult(cb.Result);
        });

        manager.Subscribe<SteamUser.SteamGuardCodeCallback>(cb =>
        {
            guardTcs.TrySetResult(true);
        });

        manager.Subscribe<SteamClient.DisconnectedCallback>(cb =>
        {
            if (!tcs.Task.IsCompleted)
                tcs.TrySetResult(EResult.Fail);
        });

        try
        {
            client.Connect();

            // Pump callbacks
            var pumpTask = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested && client.IsConnected)
                {
                    manager.RunWaitCallbacks(TimeSpan.FromMilliseconds(100));
                    await Task.Delay(50, ct);
                }
            }, ct);

            // Wait for result
            var timeout = Task.Delay(TimeSpan.FromSeconds(30), ct);
            var completed = await Task.WhenAny(tcs.Task, guardTcs.Task, timeout);

            if (completed == guardTcs.Task)
            {
                status.HasSteamGuard = true;
                status.CanLogin = true;
                status.IsBanned = false;
            }
            else if (completed == tcs.Task)
            {
                var result = tcs.Task.Result;
                status.CanLogin = result == EResult.OK;
                status.IsBanned = result == EResult.AccountDisabled || 
                                  result == EResult.AccountLocked ||
                                  result == EResult.AccountLogonDenied;
                status.HasSteamGuard = false;
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