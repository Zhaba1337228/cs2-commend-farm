using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace CommendFarm;

public class Cs2ServerManager
{
    private readonly ILogger<Cs2ServerManager> _logger;
    private Process? _serverProcess;
    private readonly string _serverDir;
    private string _lastMatchId = "";
    private bool _matchLive;
    private string _status = "not_installed";
    private string _lastError = "";
    private int _serverPort;
    private bool _installRequested;

    public bool IsRunning => _serverProcess != null && !_serverProcess.HasExited;
    public string LastMatchId => _lastMatchId;
    public bool IsInstalled => Directory.Exists(Path.Combine(_serverDir, "game", "game", "bin", "linuxsteamrt64"));

    public Cs2ServerManager(ILogger<Cs2ServerManager> logger, string? baseDir = null)
    {
        _logger = logger;
        _serverDir = baseDir ?? "/opt/cs2-server";
    }

    public async Task<bool> InstallAsync(CancellationToken ct = default)
    {
        if (IsInstalled)
        {
            _logger.LogInformation("CS2 server already installed");
            _status = "installed";
            return true;
        }

        _status = "installing";
        _installRequested = true;
        _logger.LogInformation("Installing CS2 dedicated server...");

        Directory.CreateDirectory(_serverDir);

        var steamcmdDir = Path.Combine(_serverDir, "steamcmd");
        Directory.CreateDirectory(steamcmdDir);

        // Download SteamCMD
        var psi = new ProcessStartInfo
        {
            FileName = "bash",
            Arguments = $"-c \"cd {steamcmdDir} && curl -sqL 'https://steamcdn-a.akamaihd.net/client/installer/steamcmd_linux.tar.gz' | tar zxvf -\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi)!;
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            _logger.LogError("Failed to download SteamCMD");
            _status = "install_failed";
            _lastError = "SteamCMD download failed";
            return false;
        }

        // Install CS2 server
        psi = new ProcessStartInfo
        {
            FileName = "bash",
            Arguments = $"-c \"cd {steamcmdDir} && ./steamcmd.sh +force_install_dir {_serverDir}/game +login anonymous +app_update 730 validate +quit\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process2 = Process.Start(psi)!;
        await process2.WaitForExitAsync(ct);

        if (process2.ExitCode != 0)
        {
            _logger.LogError("CS2 server install failed");
            _status = "install_failed";
            _lastError = "SteamCMD app_update failed";
            return false;
        }

        // Create server config
        var cfgDir = Path.Combine(_serverDir, "game", "game", "csgo", "cfg");
        Directory.CreateDirectory(cfgDir);
        await File.WriteAllTextAsync(Path.Combine(cfgDir, "server.cfg"), ServerCfg, ct);

        _status = "installed";
        _logger.LogInformation("CS2 server installed successfully");
        return true;
    }

    public Task StartAsync(int port = 27015, string map = "de_dust2", string? rconPassword = null, CancellationToken ct = default)
    {
        if (IsRunning)
        {
            _logger.LogWarning("Server already running");
            return Task.CompletedTask;
        }

        if (!IsInstalled)
        {
            _logger.LogError("CS2 server not installed");
            _status = "not_installed";
            return Task.CompletedTask;
        }

        var rcon = rconPassword ?? "farm" + Random.Shared.Next(1000, 9999);
        var gameDir = Path.Combine(_serverDir, "game");
        var gameExe = Path.Combine(gameDir, "game", "bin", "linuxsteamrt64", "cs2");

        if (!File.Exists(gameExe))
        {
            _logger.LogError("CS2 server executable not found at {Path}", gameExe);
            _status = "not_installed";
            _lastError = $"Executable not found: {gameExe}";
            return Task.CompletedTask;
        }

        _status = "starting";
        _serverPort = port;
        _lastError = "";
        _logger.LogInformation("Starting CS2 server on port {Port}...", port);

        _serverProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = gameExe,
                Arguments = string.Join(" ", new[]
                {
                    "-dedicated",
                    "-console",
                    "-usercon",
                    $"+game_type 0",
                    $"+game_mode 0",
                    $"+mapgroup mg_active",
                    $"+map {map}",
                    "-maxplayers 32",
                    $"-port {port}",
                    "-tickrate 64",
                    "+sv_lan 0",
                    $"-rcon_password {rcon}",
                    "+sv_cheats 0",
                    "+mp_maxrounds 1",
                    "+mp_roundtime 1",
                    "+mp_warmup_pausetimer 1",
                    "+mp_warmuptime 3600",
                }),
                WorkingDirectory = gameDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
            EnableRaisingEvents = true,
        };

        _serverProcess.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;

            // Detect when server is fully started
            if (e.Data.Contains("Connection to Steam servers successful") ||
                e.Data.Contains("VAC secure mode is activated") ||
                e.Data.Contains("Server is running"))
            {
                _status = "running";
                _logger.LogInformation("CS2 server is ready");
            }

            // Capture match_id from server logs
            var matchMatch = Regex.Match(e.Data, @"match_id[=:]\s*(\d+)");
            if (matchMatch.Success)
            {
                _lastMatchId = matchMatch.Groups[1].Value;
                _matchLive = true;
                _logger.LogInformation("Match ID captured: {MatchId}", _lastMatchId);
            }

            if (e.Data.Contains("Game Over") || e.Data.Contains("map change"))
            {
                _matchLive = false;
                _logger.LogInformation("Match ended");
            }

            _logger.LogDebug("[CS2] {Line}", e.Data);
        };

        _serverProcess.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                _lastError = e.Data;
                _logger.LogWarning("[CS2 ERR] {Line}", e.Data);
            }
        };

        _serverProcess.Exited += (_, _) =>
        {
            var code = _serverProcess?.ExitCode;
            _logger.LogInformation("CS2 server stopped (exit code {Code})", code);
            _serverProcess = null;
            _matchLive = false;
            _status = code == 0 ? "stopped" : "crashed";
            if (code != 0) _lastError = $"Server crashed with exit code {code}";
        };

        _serverProcess.Start();
        _serverProcess.BeginOutputReadLine();
        _serverProcess.BeginErrorReadLine();

        _logger.LogInformation("Server starting... RCON: {Rcon}", rcon);

        return Task.CompletedTask;
    }

    public void Stop()
    {
        if (_serverProcess != null && !_serverProcess.HasExited)
        {
            _logger.LogInformation("Stopping CS2 server...");
            try
            {
                _serverProcess.Kill(entireProcessTree: true);
            }
            catch { }
            _serverProcess = null;
            _matchLive = false;
            _status = "stopped";
        }
    }

    public ServerInfo GetInfo() => new()
    {
        IsRunning = IsRunning,
        IsInstalled = IsInstalled,
        Status = _status,
        ConnectCommand = _serverPort > 0 ? $"connect <IP>:{_serverPort}" : "",
        Port = _serverPort,
        LastMatchId = _lastMatchId,
        MatchLive = _matchLive,
        LastError = _lastError,
    };

    private const string ServerCfg = @"
// CS2 Commend Farm Server
hostname ""Commend Farm""
sv_password """"
sv_cheats 0
mp_maxrounds 1
mp_roundtime 1
mp_warmup_pausetimer 1
mp_warmuptime 3600
mp_autoteambalance 0
mp_limitteams 0
bot_quota 0
sv_hibernate_when_empty 0
sv_allow_votes 0
mp_endmatch_votenextmap 0
";
}

public class ServerInfo
{
    public bool IsRunning { get; set; }
    public bool IsInstalled { get; set; }
    public string Status { get; set; } = "not_installed";
    public string ConnectCommand { get; set; } = "";
    public int Port { get; set; }
    public string LastMatchId { get; set; } = "";
    public bool MatchLive { get; set; }
    public string LastError { get; set; } = "";
}
