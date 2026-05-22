using CommendFarm;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Information);
builder.Services.AddSingleton<AccountChecker>();

var app = builder.Build();

var dataDir = Environment.GetEnvironmentVariable("FARM_DATA_DIR") ?? ".";
var configPath = args.Length > 0 && !args[0].StartsWith("--") ? args[0] : Path.Combine(dataDir, "config.json");
var config = AppConfig.Load(configPath);

var accountsFile = Path.IsPathRooted(config.AccountsFile)
    ? config.AccountsFile
    : Path.Combine(dataDir, config.AccountsFile);
var accounts = BotAccount.LoadFromFile(accountsFile);

var sessionStore = new SessionStore(
    Path.Combine(dataDir, "sessions.json"),
    app.Services.GetRequiredService<ILogger<SessionStore>>());
sessionStore.Load();

var manager = new AccountManager(config, sessionStore,
    app.Services.GetRequiredService<ILogger<AccountManager>>());

var serverManager = new Cs2ServerManager(
    app.Services.GetRequiredService<ILogger<Cs2ServerManager>>(),
    dataDir);

WebApi.Initialize(accounts, config, manager, sessionStore, serverManager);
WebApi.Log($"Loaded {accounts.Count} accounts. Target: {config.TargetSteamId64}");

WebApi.MapEndpoints(app);

var farmCts = new CancellationTokenSource();
var loopMode = args.Contains("--loop");

if (loopMode)
{
    _ = RunFarmLoopAsync(accounts, config, manager, sessionStore, serverManager, app, farmCts.Token);
}

WebApi.Log(loopMode ? "Running in loop mode" : "API-only mode (use /api/start to begin)");

app.Urls.Add("http://0.0.0.0:5050");
WebApi.Log("Web panel: http://localhost:5050");

await app.RunAsync();

async Task RunFarmLoopAsync(
    List<BotAccount> accounts,
    AppConfig cfg,
    AccountManager mgr,
    SessionStore store,
    Cs2ServerManager server,
    WebApplication webApp,
    CancellationToken ct)
{
    WebApi.Log("Farm loop started");

    while (!ct.IsCancellationRequested)
    {
        WebApi.State.IsRunning = true;
        WebApi.State.StartedAt = DateTime.UtcNow;
        mgr.ResetCounts();

        // Update match_id from server if available
        if (server.IsRunning && !string.IsNullOrEmpty(server.LastMatchId))
        {
            if (ulong.TryParse(server.LastMatchId, out var mid))
            {
                cfg.MatchId = (int)mid;
                WebApi.Log($"Using match_id from server: {mid}");
            }
        }

        var eligible = WebApi.GetBotAccounts().Where(a => !mgr.IsOnCooldown(a.Username)).ToList();

        if (eligible.Count == 0)
        {
            WebApi.Log("All accounts on cooldown, waiting...");
            WebApi.State.IsRunning = false;
            await WebApi.WaitForWakeUpAsync(ct);
            continue;
        }

        WebApi.Log($"Running {eligible.Count} eligible accounts in batches of {cfg.BatchSize}");

        for (int i = 0; i < eligible.Count; i += cfg.BatchSize)
        {
            if (ct.IsCancellationRequested) break;

            var batch = eligible.Skip(i).Take(cfg.BatchSize).ToList();
            WebApi.Log($"Batch {i / cfg.BatchSize + 1}: {batch.Count} accounts");
            WebApi.UpdateRemaining(eligible.Count - i - batch.Count);

            foreach (var acc in batch)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    var botLogger = webApp.Services.GetRequiredService<ILogger<CommendBot>>();
                    var bot = new CommendBot(acc, cfg, store, botLogger);

                    var result = await bot.RunAsync(ct);

                    switch (result)
                    {
                        case BotResult.Success:
                            mgr.MarkCommended(acc.Username);
                            WebApi.MarkResult(acc.Username, "ok");
                            break;
                        case BotResult.Banned:
                            WebApi.MarkResult(acc.Username, "banned");
                            break;
                        case BotResult.GuardNeeded:
                        case BotResult.GuardFailed:
                            WebApi.MarkResult(acc.Username, "guard");
                            mgr.MarkFailed(acc.Username);
                            break;
                        default:
                            mgr.MarkFailed(acc.Username);
                            WebApi.MarkResult(acc.Username, "failed");
                            break;
                    }

                    await Task.Delay(cfg.LoginDelayMs, ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    mgr.MarkFailed(acc.Username);
                    WebApi.MarkResult(acc.Username, "error");
                    WebApi.Log($"ERROR [{acc.Username}]: {ex.Message}");
                }
            }

            if (i + cfg.BatchSize < eligible.Count && !ct.IsCancellationRequested)
            {
                WebApi.Log($"Batch delay {cfg.BatchDelayMs}ms...");
                await Task.Delay(cfg.BatchDelayMs, ct);
            }
        }

        WebApi.State.IsRunning = false;
        WebApi.State.LastCycleAt = DateTime.UtcNow;
        WebApi.Log($"Cycle complete. OK={mgr.SuccessCount}, FAIL={mgr.FailCount}");

        try
        {
            WebApi.Log($"Cooldown {cfg.CooldownHours}h, or until new accounts added...");
            await WebApi.WaitForWakeUpAsync(ct);
        }
        catch (OperationCanceledException) { }
    }
}
