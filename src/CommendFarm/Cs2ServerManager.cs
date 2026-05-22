using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace CommendFarm;

public class Cs2ServerManager
{
    private readonly ILogger<Cs2ServerManager> _logger;
    private string _lastMatchId = "";
    private bool _matchLive;
    private string _status = "not_installed";
    private string _lastError = "";
    private int _serverPort;

    public bool IsRunning => CheckContainerRunning();
    public string LastMatchId => _lastMatchId;
    public bool IsInstalled => true; // cm2network/cs2 image is pre-built

    public Cs2ServerManager(ILogger<Cs2ServerManager> logger, string? baseDir = null)
    {
        _logger = logger;
    }

    private bool CheckContainerRunning()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = "inspect -f '{{.State.Running}}' cs2-server 2>/dev/null",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(5000);
            return output == "true";
        }
        catch { return false; }
    }

    public Task<bool> InstallAsync(CancellationToken ct = default)
    {
        // cm2network/cs2 is pulled by docker compose, no separate install needed
        _status = "installed";
        _logger.LogInformation("CS2 server uses cm2network/cs2 Docker image (auto-pulled)");
        return Task.FromResult(true);
    }

    public Task StartAsync(int port = 27015, string map = "de_dust2", string? rconPassword = null, CancellationToken ct = default)
    {
        _serverPort = port;

        if (IsRunning)
        {
            _logger.LogWarning("CS2 server container already running");
            return Task.CompletedTask;
        }

        _status = "starting";
        _logger.LogInformation("Starting CS2 server container on port {Port}...", port);

        try
        {
            // Start just the cs2-server service
            var psi = new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = "compose up -d cs2-server 2>&1",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi)!;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(30000);

            if (process.ExitCode != 0)
            {
                _status = "install_failed";
                _lastError = output.Trim();
                _logger.LogError("Failed to start CS2 container: {Error}", output);
                return Task.CompletedTask;
            }

            _status = "running";
            _logger.LogInformation("CS2 server container started");
        }
        catch (Exception ex)
        {
            _status = "install_failed";
            _lastError = ex.Message;
            _logger.LogError("Failed to start CS2 container: {Error}", ex.Message);
        }

        return Task.CompletedTask;
    }

    public void Stop()
    {
        if (!IsRunning) return;

        _logger.LogInformation("Stopping CS2 server container...");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = "compose stop cs2-server 2>&1",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            p.WaitForExit(15000);
        }
        catch { }

        _status = "stopped";
        _matchLive = false;
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
        InstallProgress = "",
    };
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
    public string LastError { get; set; } = ""
    public string InstallProgress { get; set; } = "";
    public string? InstallLog { get; set; }
}
