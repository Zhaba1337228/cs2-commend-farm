using MailKit;
using Microsoft.Extensions.Logging;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.GC;
using SteamKit2.GC.CSGO.Internal;

namespace CommendFarm;

public class CommendBot
{
    private readonly BotAccount _account;
    private readonly AppConfig _config;
    private readonly SessionStore _sessionStore;
    private readonly ILogger<CommendBot> _logger;
    private readonly EmailVerifier _emailVerifier;

    private readonly SteamClient _steamClient;
    private readonly CallbackManager _callbackManager;
    private readonly SteamUser _steamUser;
    private readonly SteamGameCoordinator _gameCoordinator;

    private TaskCompletionSource<BotResult> _loginTcs = new();
    private readonly TaskCompletionSource<bool> _gcWelcomeTcs = new();
    private readonly TaskCompletionSource<bool> _commendResultTcs = new();
    private TaskCompletionSource<bool> _connectedTcs = new();

    private const uint CS2_APP_ID = 730;

    public CommendBot(
        BotAccount account,
        AppConfig config,
        SessionStore sessionStore,
        ILogger<CommendBot> logger)
    {
        _account = account;
        _config = config;
        _sessionStore = sessionStore;
        _logger = logger;

        var emailLogger = logger as ILogger<EmailVerifier>
            ?? new LoggerFactory().CreateLogger<EmailVerifier>();
        _emailVerifier = new EmailVerifier(emailLogger);

        _steamClient = new SteamClient();
        _callbackManager = new CallbackManager(_steamClient);
        _steamUser = _steamClient.GetHandler<SteamUser>()!;
        _gameCoordinator = _steamClient.GetHandler<SteamGameCoordinator>()!;

        RegisterCallbacks();
    }

    private void RegisterCallbacks()
    {
        _callbackManager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
        _callbackManager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
        _callbackManager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
        _callbackManager.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);
        _callbackManager.Subscribe<SteamGameCoordinator.MessageCallback>(OnGcMessage);
    }

    private async Task<bool> WaitForConnectAsync(CancellationToken ct)
    {
        if (_connectedTcs.Task.IsCompleted) return true;
        var timeout = Task.Delay(TimeSpan.FromSeconds(15), ct);
        var completed = await Task.WhenAny(_connectedTcs.Task, timeout);
        return completed == _connectedTcs.Task && _connectedTcs.Task.Result;
    }

    public async Task<BotResult> RunAsync(CancellationToken ct)
    {
        _logger.LogInformation("[{User}] Starting...", _account.Username);

        _ = RunCallbackPumpAsync(ct);

        _steamClient.Connect();

        if (!await WaitForConnectAsync(ct))
        {
            _logger.LogError("[{User}] Connection to Steam failed", _account.Username);
            return BotResult.LoginFailed;
        }

        var loginResult = await LoginAsync(ct);

        if (loginResult != BotResult.Success)
        {
            _logger.LogError("[{User}] Login failed: {Result}", _account.Username, loginResult);
            _steamClient.Disconnect();
            return loginResult;
        }

        _logger.LogInformation("[{User}] Logged in, connecting to CS2 GC...", _account.Username);

        SendClientHello();

        var gcTask = _gcWelcomeTcs.Task;
        var gcTimeout = Task.Delay(TimeSpan.FromSeconds(30), ct);
        var gcCompleted = await Task.WhenAny(gcTask, gcTimeout);

        if (gcCompleted == gcTimeout || !gcTask.IsCompleted || !gcTask.Result)
        {
            _logger.LogError("[{User}] GC welcome failed or timed out", _account.Username);
            _steamClient.Disconnect();
            return BotResult.GcTimeout;
        }

        _logger.LogInformation("[{User}] Sending commend...", _account.Username);
        SendCommend();

        var commendTask = _commendResultTcs.Task;
        var commendTimeout = Task.Delay(TimeSpan.FromSeconds(15), ct);
        var commendCompleted = await Task.WhenAny(commendTask, commendTimeout);

        if (commendCompleted == commendTimeout)
        {
            _logger.LogWarning("[{User}] Commend response timed out (may still have succeeded)", _account.Username);
        }

        _sessionStore.Update(_account.Username, d => d.LastCommendedAt = DateTime.UtcNow);
        _logger.LogInformation("[{User}] Commend sent successfully!", _account.Username);

        await Task.Delay(2000, ct);
        _steamClient.Disconnect();

        return BotResult.Success;
    }

    public async Task<BotResult> LoginOnlyAsync(CancellationToken ct)
    {
        _logger.LogInformation("[{User}] Login-only mode...", _account.Username);

        _ = RunCallbackPumpAsync(ct);

        _steamClient.Connect();

        if (!await WaitForConnectAsync(ct))
        {
            _logger.LogError("[{User}] Connection to Steam failed", _account.Username);
            return BotResult.LoginFailed;
        }

        var loginResult = await LoginAsync(ct);

        if (loginResult == BotResult.Success)
        {
            _logger.LogInformation("[{User}] Login-only succeeded, session saved", _account.Username);
            await Task.Delay(1000, ct);
        }
        else
        {
            _logger.LogError("[{User}] Login-only failed: {Result}", _account.Username, loginResult);
        }

        _steamClient.Disconnect();
        return loginResult;
    }

    private async Task<BotResult> FullRunAsync(CancellationToken ct)
    {
        _logger.LogInformation("[{User}] Starting...", _account.Username);

        _ = RunCallbackPumpAsync(ct);

        _steamClient.Connect();

        var loginResult = await LoginAsync(ct);

        if (loginResult != BotResult.Success)
        {
            _logger.LogError("[{User}] Login failed: {Result}", _account.Username, loginResult);
            _steamClient.Disconnect();
            return loginResult;
        }

        _logger.LogInformation("[{User}] Logged in, connecting to CS2 GC...", _account.Username);

        SendClientHello();

        var gcTask = _gcWelcomeTcs.Task;
        var gcTimeout = Task.Delay(TimeSpan.FromSeconds(30), ct);
        var gcCompleted = await Task.WhenAny(gcTask, gcTimeout);

        if (gcCompleted == gcTimeout || !gcTask.IsCompleted || !gcTask.Result)
        {
            _logger.LogError("[{User}] GC welcome failed or timed out", _account.Username);
            _steamClient.Disconnect();
            return BotResult.GcTimeout;
        }

        _logger.LogInformation("[{User}] Sending commend...", _account.Username);
        SendCommend();

        var commendTask = _commendResultTcs.Task;
        var commendTimeout = Task.Delay(TimeSpan.FromSeconds(15), ct);
        var commendCompleted = await Task.WhenAny(commendTask, commendTimeout);

        if (commendCompleted == commendTimeout)
        {
            _logger.LogWarning("[{User}] Commend response timed out (may still have succeeded)", _account.Username);
        }

        _sessionStore.Update(_account.Username, d => d.LastCommendedAt = DateTime.UtcNow);
        _logger.LogInformation("[{User}] Commend sent successfully!", _account.Username);

        await Task.Delay(2000, ct);
        _steamClient.Disconnect();

        return BotResult.Success;
    }

    private async Task<BotResult> LoginAsync(CancellationToken ct)
    {
        var session = _sessionStore.Get(_account.Username);

        // Try token-based login first
        if (session?.RefreshToken != null)
        {
            _logger.LogInformation("[{User}] Attempting token login...", _account.Username);

            _loginTcs = new TaskCompletionSource<BotResult>();
            _steamUser.LogOn(new SteamUser.LogOnDetails
            {
                Username = session.AccountName ?? _account.Username,
                AccessToken = session.RefreshToken,
                ShouldRememberPassword = true,
            });

            var result = await WaitForLoginResultAsync(ct);
            if (result == BotResult.Success) return BotResult.Success;

            _logger.LogWarning("[{User}] Token login failed ({Result}), falling back to credentials", _account.Username, result);
            _loginTcs = new TaskCompletionSource<BotResult>();

            // Reconnect for fresh attempt
            _steamClient.Disconnect();
            await Task.Delay(3000, ct);
            _connectedTcs = new TaskCompletionSource<bool>();
            _steamClient.Connect();

            if (!await WaitForConnectAsync(ct))
            {
                return BotResult.LoginFailed;
            }
        }

        // Credentials-based auth via SteamKit2 v3 authentication API
        _logger.LogInformation("[{User}] Authenticating with credentials...", _account.Username);

        try
        {
            var authSession = await _steamClient.Authentication.BeginAuthSessionViaCredentialsAsync(
                new AuthSessionDetails
                {
                    Username = _account.Username,
                    Password = _account.Password,
                    IsPersistentSession = true,
                    GuardData = session?.GuardData,
                    Authenticator = new EmailAuthenticator(_emailVerifier, _account, _logger),
                });

            var pollResult = await authSession.PollingWaitForResultAsync(ct);

            _logger.LogInformation("[{User}] Auth successful, logging on...", _account.Username);

            _sessionStore.Update(_account.Username, d =>
            {
                d.RefreshToken = pollResult.RefreshToken;
                d.GuardData = pollResult.NewGuardData;
                d.AccountName = pollResult.AccountName;
            });

            _loginTcs = new TaskCompletionSource<BotResult>();
            _steamUser.LogOn(new SteamUser.LogOnDetails
            {
                Username = pollResult.AccountName,
                AccessToken = pollResult.RefreshToken,
                ShouldRememberPassword = true,
            });

            var logonResult = await WaitForLoginResultAsync(ct);
            return logonResult ?? BotResult.LoginFailed;
        }
        catch (AuthenticationException ex) when (ex.Message.Contains("guard", StringComparison.OrdinalIgnoreCase) ||
                                                 ex.Message.Contains("2fa", StringComparison.OrdinalIgnoreCase) ||
                                                 ex.Message.Contains("verification", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogError("[{User}] Steam Guard failed: {Error}", _account.Username, ex.Message);
            return BotResult.GuardFailed;
        }
        catch (AuthenticationException ex)
        {
            _logger.LogError("[{User}] Authentication failed: {Error}", _account.Username, ex.Message);
            return BotResult.LoginFailed;
        }
        catch (OperationCanceledException)
        {
            _logger.LogError("[{User}] Authentication cancelled", _account.Username);
            return BotResult.LoginFailed;
        }
    }

    private async Task<BotResult?> WaitForLoginResultAsync(CancellationToken ct)
    {
        var timeout = Task.Delay(TimeSpan.FromSeconds(30), ct);
        var completed = await Task.WhenAny(_loginTcs.Task, timeout);

        if (completed == timeout) return null;
        return _loginTcs.Task.Result;
    }

    private async Task RunCallbackPumpAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                _callbackManager.RunWaitCallbacks(TimeSpan.FromMilliseconds(100));
            }
        }
        catch (OperationCanceledException) { }
    }

    private void SendClientHello()
    {
        var hello = new ClientGCMsgProtobuf<CMsgGCCStrike15_v2_MatchmakingClient2GCHello>(
            (uint)ECsgoGCMsg.k_EMsgGCCStrike15_v2_MatchmakingClient2GCHello);
        _gameCoordinator.Send(hello, CS2_APP_ID);
    }

    private void SendCommend()
    {
        var targetAccountId = SteamId64ToAccountId(_config.TargetSteamId64);

        var commend = new ClientGCMsgProtobuf<CMsgGCCStrike15_v2_ClientCommendPlayer>(
            (uint)ECsgoGCMsg.k_EMsgGCCStrike15_v2_ClientCommendPlayer);

        commend.Body.account_id = targetAccountId;
        commend.Body.match_id = _config.MatchId > 0 ? (ulong)_config.MatchId : 8;
        commend.Body.commendation = new PlayerCommendationInfo
        {
            cmd_friendly = _config.CommendFriendly ? 1u : 0u,
            cmd_teaching = _config.CommendTeacher ? 1u : 0u,
            cmd_leader = _config.CommendLeader ? 1u : 0u,
        };
        commend.Body.tokens = 10;

        _gameCoordinator.Send(commend, CS2_APP_ID);
    }

    private static uint SteamId64ToAccountId(string steamId64)
    {
        if (ulong.TryParse(steamId64, out var id))
        {
            return (uint)(id - 76561197960265728UL);
        }
        return 0;
    }

    #region Callbacks

    private void OnConnected(SteamClient.ConnectedCallback cb)
    {
        _logger.LogDebug("[{User}] Connected to Steam", _account.Username);
        _connectedTcs.TrySetResult(true);
    }

    private void OnDisconnected(SteamClient.DisconnectedCallback cb)
    {
        _logger.LogDebug("[{User}] Disconnected", _account.Username);
        _loginTcs.TrySetResult(BotResult.LoginFailed);
    }

    private void OnLoggedOn(SteamUser.LoggedOnCallback cb)
    {
        if (cb.Result != EResult.OK)
        {
            _logger.LogError("[{User}] Login failed: {Result}", _account.Username, cb.Result);
            var result = cb.Result == EResult.AccountDisabled || cb.Result == EResult.Banned
                ? BotResult.Banned
                : cb.Result == EResult.AccountLogonDenied
                    ? BotResult.GuardNeeded
                    : BotResult.LoginFailed;
            _loginTcs.TrySetResult(result);
            return;
        }

        _logger.LogInformation("[{User}] Logged on successfully", _account.Username);
        _loginTcs.TrySetResult(BotResult.Success);
    }

    private void OnLoggedOff(SteamUser.LoggedOffCallback cb)
    {
        _logger.LogDebug("[{User}] Logged off: {Result}", _account.Username, cb.Result);
    }

    private void OnGcMessage(SteamGameCoordinator.MessageCallback cb)
    {
        var msgType = MsgUtil.GetGCMsg(cb.EMsg);
        _logger.LogDebug("[{User}] GC msg: {MsgType} ({Id})", _account.Username, msgType, cb.EMsg);

        if (msgType == (uint)ECsgoGCMsg.k_EMsgGCCStrike15_v2_MatchmakingGC2ClientHello)
        {
            try
            {
                var welcome = new ClientGCMsgProtobuf<CMsgGCCStrike15_v2_MatchmakingGC2ClientHello>(cb.Message);
                var comm = welcome.Body.commendation;
                if (comm != null)
                {
                    _logger.LogInformation(
                        "[{User}] GC ready. Commends: friendly={Friendly}, teaching={Teaching}, leader={Leader}",
                        _account.Username, comm.cmd_friendly, comm.cmd_teaching, comm.cmd_leader);
                }
                else
                {
                    _logger.LogInformation("[{User}] GC ready (no commend data)", _account.Username);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug("[{User}] GC welcome parse error: {Error}", _account.Username, ex.Message);
            }

            _gcWelcomeTcs.TrySetResult(true);
        }
        else if (msgType == (uint)ECsgoGCMsg.k_EMsgGCCStrike15_v2_ClientCommendPlayerQueryResponse)
        {
            _logger.LogDebug("[{User}] Commend response received", _account.Username);
            _commendResultTcs.TrySetResult(true);
        }
    }

    #endregion
}

/// <summary>
/// IAuthenticator that fetches Steam Guard codes from email via IMAP.
/// </summary>
public class EmailAuthenticator : IAuthenticator
{
    private readonly EmailVerifier _emailVerifier;
    private readonly BotAccount _account;
    private readonly ILogger _logger;

    public EmailAuthenticator(EmailVerifier emailVerifier, BotAccount account, ILogger logger)
    {
        _emailVerifier = emailVerifier;
        _account = account;
        _logger = logger;
    }

    public async Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect)
    {
        if (!_account.HasEmail)
        {
            _logger.LogError("[{User}] Steam Guard required but no email configured", _account.Username);
            throw new AuthenticationException("No email configured for Steam Guard");
        }

        _logger.LogInformation("[{User}] Fetching Steam Guard code from email...", _account.Username);

        var code = await _emailVerifier.GetSteamGuardCodeAsync(
            _account.Email!, _account.EmailPassword!);

        if (string.IsNullOrEmpty(code))
        {
            throw new AuthenticationException("Failed to get Steam Guard code from email");
        }

        return code;
    }

    public Task<string> GetEmailCodeAsync(string emailDomain, bool previousCodeWasIncorrect)
        => GetDeviceCodeAsync(previousCodeWasIncorrect);

    public Task<bool> AcceptDeviceConfirmationAsync()
        => Task.FromResult(false);
}
