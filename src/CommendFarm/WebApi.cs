using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
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
    public string ServerNote { get; set; } = "Match ID 8 — сервер не нужен";
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
    private static TaskCompletionSource _wakeUp = new();
    private static readonly List<BotAccount> _botAccounts = new();
    private static string? _accountsFilePath;
    private static WebApplication? _appRef;

    // SSE channel for real-time events
    private static readonly Channel<SseEvent> _sseChannel = Channel.CreateUnbounded<SseEvent>();

    public static FarmState State => _state;
    public static List<BotAccount> GetBotAccounts() => _botAccounts.ToList();

    public static void Publish(string type, string data)
    {
        _sseChannel.Writer.TryWrite(new SseEvent { Type = type, Data = data, Timestamp = DateTime.UtcNow });
    }

    public static void Initialize(List<BotAccount> accounts, AppConfig config, AccountManager manager, SessionStore sessionStore)
    {
        _config = config;
        _manager = manager;
        _sessionStore = sessionStore;
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
        Publish("log", entry);
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
        Publish("account_update", $"username={username},status={status}");
    }

    public static void UpdateRemaining(int remaining)
    {
        _state.Remaining = remaining;
        Publish("progress", $"remaining={remaining}");
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
            if (!string.IsNullOrEmpty(checkStatus.Error))
                info.LastError = checkStatus.Error;
        }
        Publish("account_update", $"username={username},result={checkStatus.StatusText},error={checkStatus.Error ?? ""}");
    }

    /// <summary>Start the farm loop from Program.cs --loop mode</summary>
    public static void StartFarmLoop(WebApplication app)
    {
        if (_config == null || _manager == null || _sessionStore == null) return;

        var cts = new CancellationTokenSource();
        var cfg = _config;
        var mgr = _manager;
        var store = _sessionStore;

        Log("Farm loop started");

        Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                _state.IsRunning = true;
                _state.StartedAt = DateTime.UtcNow;
                mgr.ResetCounts();

                var eligible = _botAccounts.Where(a => !mgr.IsOnCooldown(a.Username)).ToList();

                if (eligible.Count == 0)
                {
                    Log("All accounts on cooldown, waiting...");
                    _state.IsRunning = false;
                    await WaitForWakeUpAsync(cts.Token);
                    continue;
                }

                Log($"Farm: {eligible.Count} eligible accounts");
                Publish("farm_start", $"total={eligible.Count}");

                for (int i = 0; i < eligible.Count; i += cfg.BatchSize)
                {
                    if (cts.Token.IsCancellationRequested) break;

                    var batch = eligible.Skip(i).Take(cfg.BatchSize).ToList();
                    Log($"Batch {i / cfg.BatchSize + 1}: {batch.Count} accounts");
                    UpdateRemaining(eligible.Count - i - batch.Count);
                    Publish("farm_progress", $"batch={i / cfg.BatchSize + 1},remaining={eligible.Count - i - batch.Count}");

                    foreach (var acc in batch)
                    {
                        if (cts.Token.IsCancellationRequested) break;

                        try
                        {
                            var botLogger = app.Services.GetRequiredService<ILogger<CommendBot>>();
                            var bot = new CommendBot(acc, cfg, store, botLogger);
                            var result = await bot.RunAsync(cts.Token);

                            switch (result)
                            {
                                case BotResult.Success:
                                    mgr.MarkCommended(acc.Username);
                                    MarkResult(acc.Username, "ok");
                                    Log($"[{acc.Username}] OK");
                                    break;
                                case BotResult.Banned:
                                    MarkResult(acc.Username, "banned");
                                    Log($"[{acc.Username}] BANNED");
                                    break;
                                case BotResult.GuardNeeded:
                                case BotResult.GuardFailed:
                                    MarkResult(acc.Username, "guard");
                                    mgr.MarkFailed(acc.Username);
                                    Log($"[{acc.Username}] GUARD: {bot.LastError ?? result.ToString()}");
                                    break;
                                default:
                                    mgr.MarkFailed(acc.Username);
                                    MarkResult(acc.Username, "failed");
                                    Log($"[{acc.Username}] FAIL: {bot.LastError ?? result.ToString()}");
                                    break;
                            }
                        }
                        catch (OperationCanceledException) { break; }
                        catch (Exception ex)
                        {
                            mgr.MarkFailed(acc.Username);
                            MarkResult(acc.Username, "error");
                            Log($"ERROR [{acc.Username}]: {ex.Message}");
                        }

                        await Task.Delay(cfg.LoginDelayMs, cts.Token);
                    }

                    if (i + cfg.BatchSize < eligible.Count && !cts.Token.IsCancellationRequested)
                    {
                        Log($"Batch delay {cfg.BatchDelayMs}ms...");
                        await Task.Delay(cfg.BatchDelayMs, cts.Token);
                    }
                }

                _state.IsRunning = false;
                _state.LastCycleAt = DateTime.UtcNow;
                Log($"Farm complete. OK={mgr.SuccessCount}, FAIL={mgr.FailCount}");
                Publish("farm_done", $"ok={mgr.SuccessCount},fail={mgr.FailCount}");

                Log($"Cooldown {cfg.CooldownHours}h...");
                await WaitForWakeUpAsync(cts.Token);
            }
        }, cts.Token);

        _farmCts = cts;
    }

    public static void WakeUp()
    {
        _wakeUp.TrySetResult();
        _wakeUp = new TaskCompletionSource();
        Publish("wakeup", "");
    }

    public static Task WaitForWakeUpAsync(CancellationToken ct) =>
        _wakeUp.Task;

    public static void MapEndpoints(WebApplication app)
    {
        _appRef = app;
        app.UseDefaultFiles();
        app.UseStaticFiles();

        // Real-time events SSE
        app.MapGet("/api/events", async (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers.Connection = "keep-alive";

            await foreach (var evt in _sseChannel.Reader.ReadAllAsync(ctx.RequestAborted))
            {
                if (ctx.RequestAborted.IsCancellationRequested) break;
                var json = JsonSerializer.Serialize(evt);
                await ctx.Response.WriteAsync($"data: {json}\n\n", ctx.RequestAborted);
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
            }
        });

        app.MapGet("/api/status", () => Results.Json(_state));

        app.MapGet("/api/accounts", () => Results.Json(_state.Accounts));

        // Account check — single
        app.MapPost("/api/accounts/check", async (CheckRequest req, AccountChecker checker) =>
        {
            Log($"Checking {req.Username}...");
            try
            {
                var result = await checker.CheckAsync(req.Username, req.Password, req.Email, req.EmailPassword);
                UpdateCheckResult(req.Username, result);
                Log($"Check [{req.Username}] {result.StatusText}" + (string.IsNullOrEmpty(result.Error) ? "" : $": {result.Error}"));
                return Results.Json(result);
            }
            catch (Exception ex)
            {
                Log($"Check [{req.Username}] EXCEPTION: {ex.Message}");
                return Results.Json(new AccountStatus
                {
                    Username = req.Username,
                    CanLogin = false,
                    Error = ex.Message,
                    CheckedAt = DateTime.UtcNow
                });
            }
        });

        // Check all — full progress via SSE
        app.MapPost("/api/accounts/check-all", async (AccountChecker checker, CancellationToken ct) =>
        {
            if (_config == null) return Results.Json(new { error = "Not initialized" });
            Log("Starting mass check...");
            Publish("operation_start", "type=check-all");

            var accounts = BotAccount.LoadFromFile(_config.AccountsFile);
            var results = new List<AccountStatus>();
            var total = accounts.Count;

            for (int i = 0; i < accounts.Count; i++)
            {
                if (ct.IsCancellationRequested) break;
                var acc = accounts[i];
                Log($"[{i + 1}/{total}] Checking {acc.Username}...");
                Publish("progress", $"type=check-all,current={i + 1},total={total},username={acc.Username}");

                try
                {
                    var result = await checker.CheckAsync(acc.Username, acc.Password, acc.Email, acc.EmailPassword, ct);
                    UpdateCheckResult(acc.Username, result);
                    results.Add(result);
                }
                catch (Exception ex)
                {
                    Log($"Check [{acc.Username}] EXCEPTION: {ex.Message}");
                    results.Add(new AccountStatus { Username = acc.Username, CanLogin = false, Error = ex.Message, CheckedAt = DateTime.UtcNow });
                }

                if (i < accounts.Count - 1)
                    await Task.Delay(3000, ct);
            }

            Log($"Check done: {results.Count} accounts");
            Publish("operation_done", $"type=check-all,total={results.Count}");

            var ok = results.Count(r => r.CanLogin);
            var guard = results.Count(r => r.HasSteamGuard && !r.CanLogin);
            var banned = results.Count(r => r.IsBanned);
            Log($"Summary: {ok} OK, {guard} Guard, {banned} Banned, {results.Count - ok - guard - banned} Failed");

            return Results.Json(new
            {
                total = results.Count,
                ok,
                guard,
                banned,
                failed = results.Count - ok - guard - banned,
                results
            });
        });

        // Farm start
        app.MapPost("/api/start", async (CancellationToken ct) =>
        {
            if (_state.IsRunning) return Results.Json(new { error = "Already running" });
            if (_config == null || _manager == null || _sessionStore == null)
                return Results.Json(new { error = "Not initialized" });

            Log("Starting farm via API...");
            Publish("farm_start", "");

            _farmCts?.Cancel();
            _farmCts = new CancellationTokenSource();
            var farmCt = _farmCts.Token;

            var cfg = _config;
            var mgr = _manager;
            var store = _sessionStore;

            _state.IsRunning = true;
            _state.StartedAt = DateTime.UtcNow;

            _farmTask = Task.Run(async () =>
            {
                try
                {
                    var accounts = _botAccounts.ToList();
                    var eligible = accounts.Where(a => !mgr.IsOnCooldown(a.Username)).ToList();
                    Log($"Farm: {eligible.Count} eligible accounts");
                    Publish("farm_progress", $"total={eligible.Count},remaining={eligible.Count}");

                    for (int i = 0; i < eligible.Count; i += cfg.BatchSize)
                    {
                        if (farmCt.IsCancellationRequested) break;

                        var batch = eligible.Skip(i).Take(cfg.BatchSize).ToList();
                        Log($"Batch {i / cfg.BatchSize + 1}: {batch.Count} accounts");
                        UpdateRemaining(eligible.Count - i - batch.Count);
                        Publish("farm_progress", $"batch={i / cfg.BatchSize + 1},remaining={eligible.Count - i - batch.Count}");

                        foreach (var acc in batch)
                        {
                            if (farmCt.IsCancellationRequested) break;

                            try
                            {
                                var botLogger = _appRef!.Services.GetRequiredService<ILogger<CommendBot>>();
                                var bot = new CommendBot(acc, cfg, store, botLogger);
                                var result = await bot.RunAsync(farmCt);

                                switch (result)
                                {
                                    case BotResult.Success:
                                        mgr.MarkCommended(acc.Username);
                                        MarkResult(acc.Username, "ok");
                                        Log($"[{acc.Username}] OK");
                                        break;
                                    case BotResult.Banned:
                                        MarkResult(acc.Username, "banned");
                                        Log($"[{acc.Username}] BANNED");
                                        break;
                                    case BotResult.GuardNeeded:
                                    case BotResult.GuardFailed:
                                        MarkResult(acc.Username, "guard");
                                        mgr.MarkFailed(acc.Username);
                                        Log($"[{acc.Username}] GUARD: {bot.LastError ?? result.ToString()}");
                                        break;
                                    default:
                                        mgr.MarkFailed(acc.Username);
                                        MarkResult(acc.Username, "failed");
                                        Log($"[{acc.Username}] FAIL: {bot.LastError ?? result.ToString()}");
                                        break;
                                }
                            }
                            catch (OperationCanceledException) { break; }
                            catch (Exception ex)
                            {
                                mgr.MarkFailed(acc.Username);
                                MarkResult(acc.Username, "error");
                                Log($"ERROR [{acc.Username}]: {ex.Message}");
                            }

                            await Task.Delay(cfg.LoginDelayMs, farmCt);
                        }

                        if (i + cfg.BatchSize < eligible.Count && !farmCt.IsCancellationRequested)
                        {
                            Log($"Batch delay {cfg.BatchDelayMs}ms...");
                            await Task.Delay(cfg.BatchDelayMs, farmCt);
                        }
                    }

                    Log($"Farm complete. OK={mgr.SuccessCount}, FAIL={mgr.FailCount}");
                    Publish("farm_done", $"ok={mgr.SuccessCount},fail={mgr.FailCount}");
                }
                catch (OperationCanceledException) { Log("Farm cancelled"); }
                catch (Exception ex) { Log($"Farm error: {ex.Message}"); }
                finally
                {
                    _state.IsRunning = false;
                    _state.LastCycleAt = DateTime.UtcNow;
                }
            }, farmCt);

            return Results.Json(new { status = "started" });
        });

        app.MapPost("/api/stop", () =>
        {
            _farmCts?.Cancel();
            _state.IsRunning = false;
            Log("Farm stopped");
            Publish("farm_stop", "");
            return Results.Json(new { status = "stopped" });
        });

        app.MapGet("/api/logs", () => Results.Json(_recentLogs.ToArray()));

        // Import accounts
        app.MapPost("/api/accounts/import", async (ImportAccountsRequest req, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Lines))
                return Results.Json(new { error = "No data provided" });

            var lines = req.Lines.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var added = 0;
            var skipped = 0;

            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (string.IsNullOrEmpty(line)) continue;
                var parts = line.Split(':');
                if (parts.Length < 2) { skipped++; continue; }

                var username = parts[0].Trim();
                var password = parts[1].Trim();
                var email = parts.Length >= 3 ? parts[2].Trim() : null;
                var emailPass = parts.Length >= 4 ? parts[3].Trim() : null;

                if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password)) { skipped++; continue; }
                if (_accounts.ContainsKey(username)) { skipped++; continue; }

                var acc = new BotAccount(username, password, email, emailPass);
                _botAccounts.Add(acc);

                var session = _sessionStore?.Get(username);
                var info = new AccountInfo
                {
                    Username = username,
                    Password = password,
                    Email = email,
                    EmailPassword = emailPass,
                    Status = session?.RefreshToken != null ? "ready" : "pending",
                    HasEmail = acc.HasEmail,
                    HasSession = session?.RefreshToken != null,
                };

                _accounts[username] = info;
                _state.Accounts.Add(info);
                added++;
            }

            _state.TotalAccounts = _state.Accounts.Count;
            SaveAccountsToFile();
            Log($"Imported {added} accounts ({skipped} skipped)");
            Publish("accounts_updated", $"added={added},total={_state.TotalAccounts}");
            if (added > 0) WakeUp();

            // Auto-login in background
            if (added > 0 && _config != null && _sessionStore != null)
            {
                _ = Task.Run(async () =>
                {
                    Log($"Auto-login: starting for {added} accounts...");
                    Publish("operation_start", "type=auto-login");

                    var loggedIn = 0;
                    foreach (var acc in _botAccounts.ToList())
                    {
                        if (!_accounts.TryGetValue(acc.Username, out var info) || info.Status != "pending") continue;

                        try
                        {
                            info.Status = "logging_in";
                            var botLogger = _appRef!.Services.GetRequiredService<ILogger<CommendBot>>();
                            var bot = new CommendBot(acc, _config, _sessionStore, botLogger);
                            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));

                            var result = await bot.LoginOnlyAsync(cts.Token);

                            info.HasSession = _sessionStore.Get(acc.Username)?.RefreshToken != null;
                            info.Status = result == BotResult.Success ? "ready" :
                                result == BotResult.Banned ? "banned" :
                                result == BotResult.GuardNeeded || result == BotResult.GuardFailed ? "guard" :
                                "failed";
                            info.LastUsed = DateTime.UtcNow;

                            if (result == BotResult.Success)
                            {
                                loggedIn++;
                                info.LastError = null;
                                Log($"Auto-login [{acc.Username}] OK");
                            }
                            else
                            {
                                info.LastError = bot.LastError ?? result.ToString();
                                Log($"Auto-login [{acc.Username}] FAIL: {info.LastError}");
                            }
                            Publish("account_update", $"username={acc.Username},result={info.Status}");
                        }
                        catch (Exception ex)
                        {
                            info.Status = "failed";
                            info.LastError = ex.Message;
                            Log($"Auto-login [{acc.Username}] error: {ex.Message}");
                            Publish("account_update", $"username={acc.Username},result=failed");
                        }

                        await Task.Delay(2000);
                    }
                    Log($"Auto-login done: {loggedIn}/{added} succeeded");
                    Publish("operation_done", $"type=auto-login,logged_in={loggedIn}");
                });
            }

            return Results.Json(new
            {
                added,
                skipped,
                message = added > 0 ? $"Added {added} accounts. Auto-login in background." : "No new accounts added."
            });
        });

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
            Publish("accounts_updated", $"total={_state.TotalAccounts}");
            return Results.Json(new { status = "added" });
        });

        app.MapPost("/api/accounts/remove", (RemoveAccountRequest req) =>
        {
            if (!_accounts.TryGetValue(req.Username, out var info))
                return Results.Json(new { error = "Not found" });

            _accounts.TryRemove(req.Username, out _);
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
            Publish("accounts_updated", $"total={_state.TotalAccounts}");
            return Results.Json(new { status = "removed" });
        });

        // Create sessions — login only, no commend
        app.MapPost("/api/accounts/create-sessions", async (CancellationToken ct) =>
        {
            if (_config == null || _sessionStore == null)
                return Results.Json(new { error = "Not initialized" });

            var pending = _botAccounts.Where(a =>
            {
                var session = _sessionStore.Get(a.Username);
                return session?.RefreshToken == null;
            }).ToList();

            if (pending.Count == 0)
                return Results.Json(new { status = "no_pending", message = "All accounts have sessions" });

            Log($"Creating sessions for {pending.Count} accounts...");
            Publish("operation_start", "type=create-sessions");

            _ = Task.Run(async () =>
            {
                var ok = 0;
                var fail = 0;

                for (int i = 0; i < pending.Count; i++)
                {
                    var acc = pending[i];
                    if (!_accounts.TryGetValue(acc.Username, out var info)) continue;

                    Log($"[{i + 1}/{pending.Count}] Session [{acc.Username}]...");
                    Publish("progress", $"type=create-sessions,current={i + 1},total={pending.Count},username={acc.Username}");

                    try
                    {
                        info.Status = "logging_in";
                        info.LastError = null;

                        var botLogger = _appRef!.Services.GetRequiredService<ILogger<CommendBot>>();
                        var bot = new CommendBot(acc, _config, _sessionStore, botLogger);
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));

                        var result = await bot.LoginOnlyAsync(cts.Token);

                        info.HasSession = _sessionStore.Get(acc.Username)?.RefreshToken != null;
                        info.Status = result == BotResult.Success ? "ready" :
                            result == BotResult.Banned ? "banned" :
                            result == BotResult.GuardNeeded || result == BotResult.GuardFailed ? "guard" :
                            "failed";

                        if (result == BotResult.Success)
                        {
                            ok++;
                            info.LastError = null;
                            Log($"Session [{acc.Username}] OK");
                        }
                        else
                        {
                            fail++;
                            info.LastError = bot.LastError ?? result.ToString();
                            Log($"Session [{acc.Username}] FAIL: {info.LastError}");
                        }
                        Publish("account_update", $"username={acc.Username},result={info.Status}");
                    }
                    catch (OperationCanceledException)
                    {
                        fail++;
                        info.Status = "failed";
                        info.LastError = "Timeout (90s)";
                        Log($"Session [{acc.Username}] TIMEOUT");
                        Publish("account_update", $"username={acc.Username},result=timeout");
                    }
                    catch (Exception ex)
                    {
                        fail++;
                        info.Status = "failed";
                        info.LastError = ex.Message;
                        Log($"Session [{acc.Username}] ERROR: {ex.Message}");
                        Publish("account_update", $"username={acc.Username},result=error");
                    }

                    await Task.Delay(2000, ct);
                }

                Log($"Sessions done: {ok} OK, {fail} FAIL out of {pending.Count}");
                Publish("operation_done", $"type=create-sessions,ok={ok},fail={fail}");
            }, ct);

            return Results.Json(new
            {
                status = "started",
                pending = pending.Count,
                message = $"Session creation started for {pending.Count} accounts. Monitor via /api/events."
            });
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
            Publish("operation_done", "type=clear-sessions");
            return Results.Json(new { status = "cleared" });
        });

        app.MapGet("/api/config", () => Results.Json(_config));
        app.MapPost("/api/rcon/check", async (RconCheckRequest? req, CancellationToken ct) =>
        {
            if (_config == null) return Results.Json(new { error = "Not initialized" });

            var host = req?.Host ?? _config.RconHost ?? "";
            var port = req?.Port ?? _config.RconPort;
            var password = req?.Password ?? _config.RconPassword ?? "";

            if (string.IsNullOrEmpty(host))
                return Results.Json(new { error = "No RCON host configured. Set RconHost in config." });

            using var rcon = new RconClient();
            var result = await rcon.CheckServerAsync(host, port, password, ct);
            return Results.Json(result);
        });

        app.MapPost("/api/rcon/match-id", async (RconCheckRequest? req, CancellationToken ct) =>
        {
            if (_config == null) return Results.Json(new { error = "Not initialized" });

            var host = req?.Host ?? _config.RconHost ?? "";
            var port = req?.Port ?? _config.RconPort;
            var password = req?.Password ?? _config.RconPassword ?? "";

            if (string.IsNullOrEmpty(host))
                return Results.Json(new { error = "No RCON host configured" });

            using var rcon = new RconClient();
            var connected = await rcon.ConnectAsync(host, port, password, ct);
            if (!connected)
                return Results.Json(new { error = rcon.LastError ?? "Connection failed" });

            var matchInfo = await rcon.GetMatchInfoAsync(ct);
            rcon.Disconnect();

            return Results.Json(new
            {
                host,
                port,
                map = matchInfo?.ServerInfo?.Map,
                players = matchInfo?.ServerInfo?.Players,
                playerList = matchInfo?.ServerInfo?.PlayersList,
                rawStatus = matchInfo?.ServerStatus
            });
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
            if (req.RconHost != null) _config.RconHost = string.IsNullOrWhiteSpace(req.RconHost) ? null : req.RconHost;
            if (req.RconPort.HasValue) _config.RconPort = req.RconPort.Value;
            if (req.RconPassword != null) _config.RconPassword = string.IsNullOrWhiteSpace(req.RconPassword) ? null : req.RconPassword;
            if (req.UseRconMatchId.HasValue) _config.UseRconMatchId = req.UseRconMatchId.Value;
            _state.TargetSteamId64 = _config.TargetSteamId64;

            var configPath = Path.IsPathRooted(_config.AccountsFile)
                ? Path.Combine(Path.GetDirectoryName(_config.AccountsFile) ?? ".", "config.json")
                : Path.Combine(Path.GetDirectoryName(_accountsFilePath ?? "accounts.txt") ?? ".", "config.json");
            _config.Save(configPath);
            Log($"Config updated. MatchId={_config.MatchId}, RCON={_config.RconHost ?? "none"}");
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

public record CheckRequest(string Username, string Password, string? Email = null, string? EmailPassword = null);
public record AddAccountRequest(string Username, string Password, string? Email = null, string? EmailPassword = null);
public record ImportAccountsRequest(string Lines);
public record RemoveAccountRequest(string Username, bool ClearSession = false);
public record UpdateConfigRequest(
    string? TargetSteamId64 = null,
    int? MatchId = null,
    int? CooldownHours = null,
    int? LoginDelayMs = null,
    int? BatchSize = null,
    int? BatchDelayMs = null,
    string? RconHost = null,
    int? RconPort = null,
    string? RconPassword = null,
    bool? UseRconMatchId = null);

public record RconCheckRequest(string? Host = null, int? Port = null, string? Password = null);

public record SseEvent
{
    public string Type { get; set; } = "";
    public string Data { get; set; } = "";
    public DateTime Timestamp { get; set; }
}

public record ServerInfo
{
    public string Status { get; set; } = "";
    public string Message { get; set; } = "";
}