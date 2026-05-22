using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CommendFarm;

public class FarmState
{
    public bool IsRunning { get; set; }
    public int TotalAccounts { get; set; }
    public int SuccessCount { get; set; }
    public int FailCount { get; set; }
    public int Remaining { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? LastCycleAt { get; set; }
    public string TargetSteamId64 { get; set; } = "";
    public List<AccountInfo> Accounts { get; set; } = new();
    public ServerInfo? Server { get; set; }
}

public class AccountInfo
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string? Email { get; set; }
    public string? EmailPassword { get; set; }
    public string Status { get; set; } = "unknown";
    public bool HasEmail { get; set; }
    public bool HasSession { get; set; }
    public int TimesUsed { get; set; }
    public DateTime? LastUsed { get; set; }
    public DateTime? LastChecked { get; set; }
    public string? CheckResult { get; set; }
    public string? LastError { get; set; }
}

public static class WebApi
{
    private static FarmState _state = new();
    private static readonly ConcurrentDictionary<string, AccountInfo> _accounts = new();
    private static readonly ConcurrentQueue<string> _recentLogs = new();
    private static CancellationTokenSource? _farmCts;
    private static Task? _farmTask;
    private static AccountManager? _manager;
    private static SessionStore? _sessionStore;
    private static AppConfig? _config;
    private static Cs2ServerManager? _serverManager;
    private static readonly List<BotAccount> _botAccounts = new();
    private static string? _accountsFilePath;

    public static FarmState State => _state;

    public static void Initialize(List<BotAccount> accounts, AppConfig config, AccountManager manager, SessionStore sessionStore, Cs2ServerManager serverManager)
    {
        _config = config;
        _manager = manager;
        _sessionStore = sessionStore;
        _serverManager = serverManager;
        _accountsFilePath = Path.IsPathRooted(config.AccountsFile)
            ? config.AccountsFile
            : Path.Combine(Path.GetDirectoryName(config.AccountsFile) ?? ".", config.AccountsFile);

        _botAccounts.Clear();
        _botAccounts.AddRange(accounts);

        _state.TotalAccounts = accounts.Count;
        _state.TargetSteamId64 = config.TargetSteamId64;
        _state.Accounts.Clear();
        _accounts.Clear();

        foreach (var acc in accounts)
        {
            var session = sessionStore.Get(acc.Username);
            var info = new AccountInfo
            {
                Username = acc.Username,
                Password = acc.Password,
                Email = acc.Email,
                EmailPassword = acc.EmailPassword,
                Status = manager.IsOnCooldown(acc.Username) ? "cooldown" : "ready",
                HasEmail = acc.HasEmail,
                HasSession = session?.RefreshToken != null,
                LastUsed = session?.LastCommendedAt,
            };
            _state.Accounts.Add(info);
            _accounts[acc.Username] = info;
        }
    }

    public static void Log(string message)
    {
        var entry = $"[{DateTime.UtcNow:HH:mm:ss}] {message}";
        _recentLogs.Enqueue(entry);
        while (_recentLogs.Count > 500)
            _recentLogs.TryDequeue(out _);
    }

    private static void SaveAccountsToFile()
    {
        if (string.IsNullOrEmpty(_accountsFilePath)) return;
        try
        {
            var lines = _botAccounts.Select(a =>
            {
                if (a.HasEmail)
                    return $"{a.Username}:{a.Password}:{a.Email}:{a.EmailPassword}";
                return $"{a.Username}:{a.Password}";
            });
            File.WriteAllLines(_accountsFilePath, lines);
        }
        catch (Exception ex)
        {
            Log($"Failed to save accounts: {ex.Message}");
        }
    }

    public static void MarkResult(string username, string status)
    {
        if (status == "ok")
            _state.SuccessCount++;
        else
            _state.FailCount++;

        if (_accounts.TryGetValue(username, out var info))
        {
            info.Status = status;
            info.TimesUsed++;
            info.LastUsed = DateTime.UtcNow;
            info.HasSession = _sessionStore?.Get(username)?.RefreshToken != null;
        }
    }

    public static void UpdateRemaining(int remaining)
    {
        _state.Remaining = remaining;
    }

    public static void UpdateCheckResult(string username, AccountStatus checkStatus)
    {
        if (_accounts.TryGetValue(username, out var info))
        {
            info.LastChecked = checkStatus.CheckedAt;
            info.CheckResult = checkStatus.StatusText;
            info.Status = checkStatus.IsBanned ? "banned" :
                checkStatus.HasSteamGuard ? "guard" :
                checkStatus.CanLogin ? "ok" : "failed";
        }
    }

    private static void UpdateServerState()
    {
        if (_serverManager != null)
            _state.Server = _serverManager.GetInfo();
    }

    public static void MapEndpoints(WebApplication app)
    {
        app.UseDefaultFiles();
        app.UseStaticFiles();

        app.MapGet("/api/status", () =>
        {
            UpdateServerState();
            return Results.Json(_state);
        });

        app.MapGet("/api/accounts", () => Results.Json(_state.Accounts));

        // Server management
        app.MapPost("/api/server/install", async () =>
        {
            if (_serverManager == null) return Results.Json(new { error = "Not initialized" });
            Log("Installing CS2 server...");
            var ok = await _serverManager.InstallAsync();
            return ok ? Results.Json(new { status = "installed" }) : Results.Json(new { error = "Install failed" });
        });

        app.MapPost("/api/server/start", (ServerStartRequest? req) =>
        {
            if (_serverManager == null) return Results.Json(new { error = "Not initialized" });
            if (_serverManager.IsRunning) return Results.Json(new { error = "Already running" });
            Log($"Starting CS2 server on port {req?.Port ?? 27015}...");
            _serverManager.StartAsync(req?.Port ?? 27015, req?.Map ?? "de_dust2", req?.RconPassword);
            return Results.Json(new { status = "started" });
        });

        app.MapPost("/api/server/stop", () =>
        {
            if (_serverManager == null) return Results.Json(new { error = "Not initialized" });
            _serverManager.Stop();
            Log("CS2 server stopped");
            return Results.Json(new { status = "stopped" });
        });

        app.MapGet("/api/server/status", () =>
        {
            if (_serverManager == null) return Results.Json(new { error = "Not initialized" });
            return Results.Json(_serverManager.GetInfo());
        });

        // Account check
        app.MapPost("/api/accounts/check", async (CheckRequest req, AccountChecker checker) =>
        {
            Log($"Checking {req.Username}...");
            var result = await checker.CheckAsync(req.Username, req.Password);
            UpdateCheckResult(req.Username, result);
            return Results.Json(result);
        });

        app.MapPost("/api/accounts/check-all", async (AccountChecker checker) =>
        {
            if (_config == null) return Results.Json(new { error = "Not initialized" });
            Log("Starting mass check...");
            var accounts = BotAccount.LoadFromFile(_config.AccountsFile);
            var results = new List<AccountStatus>();

            foreach (var acc in accounts)
            {
                Log($"Checking {acc.Username}...");
                var result = await checker.CheckAsync(acc.Username, acc.Password);
                UpdateCheckResult(acc.Username, result);
                results.Add(result);
                await Task.Delay(1000);
            }

            Log($"Check done: {results.Count} accounts");
            return Results.Json(new { total = results.Count, results });
        });

        // Farm control
        app.MapPost("/api/start", () =>
        {
            if (_state.IsRunning)
                return Results.Json(new { error = "Already running" });

            if (_config == null || _manager == null || _sessionStore == null)
                return Results.Json(new { error = "Not initialized" });

            Log("Starting farm via API...");

            _farmCts?.Cancel();
            _farmCts = new CancellationTokenSource();
            var ct = _farmCts.Token;

            var cfg = _config;
            var mgr = _manager;
            var store = _sessionStore;
            var server = _serverManager;

            _state.IsRunning = true;
            _state.StartedAt = DateTime.UtcNow;

            _farmTask = Task.Run(async () =>
            {
                try
                {
                    var accounts = _botAccounts.ToList();

                    // Use match_id from server if available
                    if (server?.IsRunning == true && !string.IsNullOrEmpty(server.LastMatchId))
                    {
                        if (ulong.TryParse(server.LastMatchId, out var mid))
                        {
                            cfg.MatchId = (int)mid;
                            Log($"Using match_id from server: {mid}");
                        }
                    }

                    var eligible = accounts.Where(a => !mgr.IsOnCooldown(a.Username)).ToList();
                    Log($"Farm: {eligible.Count} eligible accounts");

                    for (int i = 0; i < eligible.Count; i += cfg.BatchSize)
                    {
                        if (ct.IsCancellationRequested) break;

                        var batch = eligible.Skip(i).Take(cfg.BatchSize).ToList();
                        Log($"Batch {i / cfg.BatchSize + 1}: {batch.Count} accounts");
                        UpdateRemaining(eligible.Count - i - batch.Count);

                        foreach (var acc in batch)
                        {
                            if (ct.IsCancellationRequested) break;

                            try
                            {
                                var botLogger = app.Services.GetRequiredService<ILogger<CommendBot>>();
                                var bot = new CommendBot(acc, cfg, store, botLogger);

                                var result = await bot.RunAsync(ct);

                                switch (result)
                                {
                                    case BotResult.Success:
                                        mgr.MarkCommended(acc.Username);
                                        MarkResult(acc.Username, "ok");
                                        break;
                                    case BotResult.Banned:
                                        MarkResult(acc.Username, "banned");
                                        break;
                                    case BotResult.GuardNeeded:
                                    case BotResult.GuardFailed:
                                        MarkResult(acc.Username, "guard");
                                        mgr.MarkFailed(acc.Username);
                                        break;
                                    default:
                                        mgr.MarkFailed(acc.Username);
                                        MarkResult(acc.Username, "failed");
                                        break;
                                }

                                await Task.Delay(cfg.LoginDelayMs, ct);
                            }
                            catch (OperationCanceledException) { break; }
                            catch (Exception ex)
                            {
                                mgr.MarkFailed(acc.Username);
                                MarkResult(acc.Username, "error");
                                Log($"ERROR [{acc.Username}]: {ex.Message}");
                            }
                        }

                        if (i + cfg.BatchSize < eligible.Count && !ct.IsCancellationRequested)
                        {
                            Log($"Batch delay {cfg.BatchDelayMs}ms...");
                            await Task.Delay(cfg.BatchDelayMs, ct);
                        }
                    }

                    Log($"Farm complete. OK={mgr.SuccessCount}, FAIL={mgr.FailCount}");
                }
                catch (OperationCanceledException) { Log("Farm cancelled"); }
                catch (Exception ex) { Log($"Farm error: {ex.Message}"); }
                finally
                {
                    _state.IsRunning = false;
                    _state.LastCycleAt = DateTime.UtcNow;
                }
            }, ct);

            return Results.Json(new { status = "started" });
        });

        app.MapPost("/api/stop", () =>
        {
            _farmCts?.Cancel();
            _state.IsRunning = false;
            Log("Farm stopped");
            return Results.Json(new { status = "stopped" });
        });

        app.MapGet("/api/logs", () => Results.Json(_recentLogs.ToArray()));

        // Account management
        app.MapPost("/api/accounts/add", (AddAccountRequest req) =>
        {
            if (string.IsNullOrEmpty(req.Username) || string.IsNullOrEmpty(req.Password))
                return Results.Json(new { error = "Username and password required" });

            if (_accounts.ContainsKey(req.Username))
                return Results.Json(new { error = "Already exists" });

            var acc = new BotAccount(req.Username, req.Password, req.Email, req.EmailPassword);
            _botAccounts.Add(acc);

            var session = _sessionStore?.Get(req.Username);
            var info = new AccountInfo
            {
                Username = req.Username,
                Password = req.Password,
                Email = req.Email,
                EmailPassword = req.EmailPassword,
                Status = "ready",
                HasEmail = acc.HasEmail,
                HasSession = session?.RefreshToken != null,
            };

            _accounts[req.Username] = info;
            _state.Accounts.Add(info);
            _state.TotalAccounts = _state.Accounts.Count;

            SaveAccountsToFile();
            Log($"Added account: {req.Username}");
            return Results.Json(new { status = "added" });
        });

        app.MapPost("/api/accounts/remove", (RemoveAccountRequest req) =>
        {
            if (!_accounts.TryGetValue(req.Username, out var info))
                return Results.Json(new { error = "Not found" });

            _accounts.Remove(req.Username);
            _state.Accounts.Remove(info);
            _state.TotalAccounts = _state.Accounts.Count;
            _botAccounts.RemoveAll(a => a.Username == req.Username);

            if (req.ClearSession && _sessionStore != null)
            {
                var session = _sessionStore.Get(req.Username);
                if (session != null)
                {
                    session.RefreshToken = null;
                    session.GuardData = null;
                    _sessionStore.Save();
                }
                Log($"Removed account + session: {req.Username}");
            }
            else
            {
                Log($"Removed account: {req.Username}");
            }

            SaveAccountsToFile();
            return Results.Json(new { status = "removed" });
        });

        app.MapPost("/api/accounts/clear-sessions", () =>
        {
            if (_sessionStore == null) return Results.Json(new { error = "Not initialized" });
            foreach (var acc in _state.Accounts)
            {
                var session = _sessionStore.Get(acc.Username);
                if (session != null)
                {
                    session.RefreshToken = null;
                    session.GuardData = null;
                    acc.HasSession = false;
                }
            }
            _sessionStore.Save();
            Log("Cleared all sessions");
            return Results.Json(new { status = "cleared" });
        });

        // Config
        app.MapPost("/api/config/update", (UpdateConfigRequest req) =>
        {
            if (_config == null) return Results.Json(new { error = "Not initialized" });
            if (req.TargetSteamId64 != null) _config.TargetSteamId64 = req.TargetSteamId64;
            if (req.CooldownHours.HasValue) _config.CooldownHours = req.CooldownHours.Value;
            if (req.LoginDelayMs.HasValue) _config.LoginDelayMs = req.LoginDelayMs.Value;
            if (req.BatchSize.HasValue) _config.BatchSize = req.BatchSize.Value;
            if (req.BatchDelayMs.HasValue) _config.BatchDelayMs = req.BatchDelayMs.Value;
            if (req.MatchId.HasValue) _config.MatchId = req.MatchId.Value;
            _state.TargetSteamId64 = _config.TargetSteamId64;

            var json = JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText("config.json", json);
            Log($"Config updated");
            return Results.Json(new { status = "updated" });
        });

        app.MapGet("/api/sessions", () =>
        {
            if (_sessionStore == null) return Results.Json(new { error = "Not initialized" });
            var accounts = _state.Accounts.Select(a => new
            {
                a.Username,
                HasSession = _sessionStore.Get(a.Username)?.RefreshToken != null,
                LastCommended = _sessionStore.Get(a.Username)?.LastCommendedAt,
            });
            return Results.Json(accounts);
        });
    }
}

public record CheckRequest(string Username, string Password);

public record AddAccountRequest(string Username, string Password, string? Email = null, string? EmailPassword = null);

public record RemoveAccountRequest(string Username, bool ClearSession = false);

public record UpdateConfigRequest(
    string? TargetSteamId64 = null,
    int? CooldownHours = null,
    int? LoginDelayMs = null,
    int? BatchSize = null,
    int? BatchDelayMs = null,
    int? MatchId = null);

public record ServerStartRequest(int? Port = null, string? Map = null, string? RconPassword = null);
