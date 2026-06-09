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
        string username, string password, string? email = null, string? emailPassword = null, CancellationToken ct = default)
    {
        var status = new AccountStatus
        {
            Username = username,
            CheckedAt = DateTime.UtcNow
        };

        var client = new SteamClient(SteamConfiguration.Create(config =>
            config.WithProtocolTypes(SteamKit2.ProtocolTypes.WebSocket)));
        var manager = new CallbackManager(client);
        var user = client.GetHandler<SteamUser>()!;

        var tcs = new TaskCompletionSource<BotResult>();
        var connected = new TaskCompletionSource<bool>();
        var guardFailed = false;
        var guardData = (string?)null;

        manager.Subscribe<SteamClient.ConnectedCallback>(cb =>
        {
            connected.TrySetResult(true);
        });

        manager.Subscribe<SteamClient.DisconnectedCallback>(cb =>
        {
            if (!tcs.Task.IsCompleted)
                tcs.TrySetResult(BotResult.LoginFailed);
        });

        manager.Subscribe<SteamUser.LoggedOnCallback>(cb =>
        {
            if (cb.Result == EResult.OK)
            {
                tcs.TrySetResult(BotResult.Success);
            }
            else if (cb.Result == EResult.AccountLogonDenied || cb.Result == EResult.AccountLoginDeniedNeedTwoFactor)
            {
                // Guard needed — this is OK, account is valid
                tcs.TrySetResult(BotResult.GuardNeeded);
            }
            else if (cb.Result == EResult.AccountDisabled || cb.Result == EResult.Banned)
            {
                tcs.TrySetResult(BotResult.Banned);
            }
            else
            {
                tcs.TrySetResult(BotResult.LoginFailed);
            }
        });

        try
        {
            client.Connect();

            // Wait for connection - must complete successfully
            var connTimeout = Task.Delay(TimeSpan.FromSeconds(15), ct);
            var connDone = await Task.WhenAny(connected.Task, connTimeout);
            if (connDone != connected.Task || !connected.Task.IsCompletedSuccessfully)
            {
                status.CanLogin = false;
                status.Error = "Connection timeout (15s)";
                client.Disconnect();
                return status;
            }

            // Build authenticator if email provided
            IAuthenticator? authenticator = null;
            EmailVerifier? emailVerifier = null;

            if (!string.IsNullOrEmpty(email) && !string.IsNullOrEmpty(emailPassword))
            {
                emailVerifier = new EmailVerifier(_logger as ILogger<EmailVerifier>
                    ?? new LoggerFactory().CreateLogger<EmailVerifier>());
                var botLogger = _logger as ILogger<CommendBot>
                    ?? new LoggerFactory().CreateLogger<CommendBot>();
                var botAccount = new BotAccount(username, password, email, emailPassword);
                authenticator = new EmailAuthenticator(emailVerifier, botAccount, botLogger);
            }

            // SteamKit2 v3 authentication
            var authSession = await client.Authentication.BeginAuthSessionViaCredentialsAsync(
                new AuthSessionDetails
                {
                    Username = username,
                    Password = password,
                    IsPersistentSession = true,
                    Authenticator = authenticator,
                });

            // If no email authenticator, guard will be needed
            if (authenticator == null)
            {
                status.HasSteamGuard = true;
                status.CanLogin = false;
                status.Error = "Steam Guard required, no email configured";
                client.Disconnect();
                return status;
            }

            // Poll for auth result
            try
            {
                var pollResult = await authSession.PollingWaitForResultAsync(ct);
                guardData = pollResult.NewGuardData;

                // Auth succeeded — now do SteamUser logon with tokens
                user.LogOn(new SteamUser.LogOnDetails
                {
                    Username = pollResult.AccountName ?? username,
                    AccessToken = pollResult.RefreshToken,
                    ShouldRememberPassword = true,
                });

                var loginDone = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(20), ct));
                if (loginDone == tcs.Task)
                {
                    var result = tcs.Task.Result;
                    status.HasSteamGuard = guardFailed;
                    status.CanLogin = result == BotResult.Success;
                    status.IsBanned = result == BotResult.Banned;
                }
                else
                {
                    status.CanLogin = false;
                    status.Error = "Login timeout after auth";
                }
            }
            catch (AuthenticationException ex) when (ex.Message.Contains("guard", StringComparison.OrdinalIgnoreCase) ||
                                                     ex.Message.Contains("2fa", StringComparison.OrdinalIgnoreCase) ||
                                                     ex.Message.Contains("verification", StringComparison.OrdinalIgnoreCase))
            {
                status.HasSteamGuard = true;
                status.CanLogin = false;
                status.Error = $"Guard: {ex.Message}";
                guardFailed = true;
            }
            catch (AuthenticationException ex)
            {
                status.CanLogin = false;
                status.Error = $"Auth: {ex.Message}";
            }
        }
        catch (OperationCanceledException)
        {
            status.CanLogin = false;
            status.Error = "Cancelled";
        }
        catch (Exception ex)
        {
            status.CanLogin = false;
            status.Error = ex.Message;
        }
        finally
        {
            try { client.Disconnect(); } catch { }
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