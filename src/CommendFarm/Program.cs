using CommendFarm;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Information);
builder.Logging.AddFilter("SteamKit2", LogLevel.Warning);
builder.Logging.AddFilter("System.Net", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
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

WebApi.Initialize(accounts, config, manager, sessionStore);
WebApi.Log($"Loaded {accounts.Count} accounts. Target: {config.TargetSteamId64}");

WebApi.MapEndpoints(app);

var loopMode = args.Contains("--loop");
if (loopMode)
{
    WebApi.StartFarmLoop(app);
}

WebApi.Log(loopMode ? "Running in loop mode" : "API-only mode (use /api/start to begin)");

app.Urls.Add("http://0.0.0.0:5050");
WebApi.Log("Web panel: http://localhost:5050");

await app.RunAsync();