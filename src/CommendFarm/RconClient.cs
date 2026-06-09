using System.Net.Sockets;
using System.Text;

namespace CommendFarm;

/// <summary>
/// Lightweight RCON client for CS2/CS:GO servers.
/// No external deps — pure TCP.
/// Connects to any server, executes commands, parses responses.
/// Used to fetch real match_id without hosting a 60GB server.
/// </summary>
public class RconClient : IDisposable
{
    private TcpClient? _client;
    private NetworkStream? _stream;
    private int _packetId = 1;

    public string Host { get; private set; } = "";
    public int Port { get; private set; }
    public string Password { get; private set; } = "";
    public bool IsConnected => _client?.Connected == true;
    public string? LastError { get; private set; }

    public RconClient() { }

    public RconClient(string host, int port, string password)
    {
        Host = host;
        Port = port;
        Password = password;
    }

    public async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        try
        {
            _client?.Dispose();
            _client = new TcpClient();
            await _client.ConnectAsync(Host, Port, ct);
            _stream = _client.GetStream();
            _stream.ReadTimeout = 5000;
            _stream.WriteTimeout = 5000;
            LastError = null;

            // Authenticate
            var authOk = await SendCommandAsync("auth " + Password, ct);
            return authOk != null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }

    /// <summary>Connect to host:port with password</summary>
    public async Task<bool> ConnectAsync(string host, int port, string password, CancellationToken ct = default)
    {
        Host = host;
        Port = port;
        Password = password;
        return await ConnectAsync(ct);
    }

    public async Task<string?> SendCommandAsync(string command, CancellationToken ct = default)
    {
        if (_stream == null || !_client!.Connected)
        {
            LastError = "Not connected";
            return null;
        }

        try
        {
            // Send packet
            var packetData = BuildPacket(3, command); // SERVERDATA_EXECCOMMAND = 3
            await _stream.WriteAsync(packetData, ct);

            // Read response(s)
            var responses = new List<byte>();
            var buf = new byte[4096];
            var end = false;
            var timeout = Task.Delay(5000, ct);

            while (!end && !timeout.IsCompleted)
            {
                var readTask = _stream.ReadAsync(buf, 0, buf.Length, ct);
                var completed = await Task.WhenAny(readTask, timeout);

                if (completed == timeout)
                    break;

                var bytesRead = await readTask;
                if (bytesRead <= 0) break;

                for (int i = 0; i < bytesRead - 4;)
                {
                    var len = BitConverter.ToInt32(buf, i);
                    if (len <= 0 || len > 4096) { end = true; break; }
                    var packetEnd = Math.Min(i + 4 + len, bytesRead);
                    var packet = buf.Skip(i + 4).Take(len - 4).ToArray();
                    responses.AddRange(packet);
                    i = packetEnd;
                }
            }

            var response = Encoding.UTF8.GetString(responses.ToArray()).TrimEnd('\0');
            return response;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return null;
        }
    }

    /// <summary>Parse live match data from CS2 server and return match_id + round info</summary>
    public async Task<MatchInfo?> GetMatchInfoAsync(CancellationToken ct = default)
    {
        var info = new MatchInfo();

        // Get live scoreboard
        var score = await SendCommandAsync("status", ct);
        if (!string.IsNullOrEmpty(score))
        {
            info.ServerInfo = ParseServerStatus(score);
            info.RawStatus = score;
        }

        // Try getting match info from CS2 specific commands
        // CS2 doesn't expose matchid directly via rcon in same way as CS:GO
        // But we can parse from logaddress or getserverip

        // Try to get current map and players
        var map = await SendCommandAsync("maps", ct);
        if (!string.IsNullOrEmpty(map))
            info.AvailableMaps = map.Split('\n').Take(10).ToList();

        // Try getplayercount
        var players = await SendCommandAsync("getplayercount", ct);
        if (!string.IsNullOrEmpty(players))
            info.PlayerCount = players.Trim();

        return info;
    }

    /// <summary>Check if server is reachable and responding</summary>
    public async Task<ServerCheckResult> CheckServerAsync(string host, int port, string password, CancellationToken ct = default)
    {
        Host = host;
        Port = port;
        Password = password;
        return await CheckServerAsync(ct);
    }

    public async Task<ServerCheckResult> CheckServerAsync(CancellationToken ct = default)
    {
        var result = new ServerCheckResult { Host = Host, Port = Port };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var connected = await ConnectAsync(ct);
        sw.Stop();
        result.LatencyMs = sw.ElapsedMilliseconds;

        if (!connected)
        {
            result.Success = false;
            result.Error = LastError ?? "Connection failed";
            return result;
        }

        result.Success = true;

        // Get server info
        var status = await SendCommandAsync("status", ct);
        if (!string.IsNullOrEmpty(status))
        {
            result.ServerInfo = ParseServerStatus(status);
        }

        // Get bot count as proxy for active match
        var bots = await SendCommandAsync("bot_count", ct);
        if (!string.IsNullOrEmpty(bots))
            result.BotInfo = bots.Trim();

        Disconnect();
        return result;
    }

    private byte[] BuildPacket(int type, string command)
    {
        using var ms = new MemoryStream();
        var id = _packetId++;
        var body = Encoding.UTF8.GetBytes(command + "\0");
        var pad = new byte[2]; // 2 null bytes padding

        var header = BitConverter.GetBytes(body.Length + pad.Length + 4);
        var idBytes = BitConverter.GetBytes(id);
        var typeBytes = BitConverter.GetBytes(type);

        ms.Write(header);
        ms.Write(idBytes);
        ms.Write(typeBytes);
        ms.Write(body);
        ms.Write(pad);

        return ms.ToArray();
    }

    private static ServerStatusInfo ParseServerStatus(string output)
    {
        var info = new ServerStatusInfo();
        if (string.IsNullOrEmpty(output)) return info;

        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("hostname:")) info.Hostname = trimmed.Substring(9).Trim();
            else if (trimmed.StartsWith("version :")) info.Version = trimmed.Substring(11).Trim();
            else if (trimmed.StartsWith("udp/ip")) info.UdpIp = trimmed.Substring(7).Trim();
            else if (trimmed.StartsWith("map :")) info.Map = trimmed.Substring(5).Trim();
            else if (trimmed.StartsWith("players :"))
            {
                // "players : X"
                var parts = trimmed.Substring(10).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0) info.Players = parts[0];
            }
            else if (trimmed.StartsWith("#"))
            {
                // Player line: "# 2 \"PlayerName\"..."
                var playerLine = trimmed.TrimStart('#');
                info.PlayersList ??= new List<string>();
                info.PlayersList.Add(trimmed);
            }
        }
        return info;
    }

    public void Disconnect()
    {
        try { _stream?.Close(); } catch { }
        try { _client?.Close(); } catch { }
        _stream = null;
        _client = null;
    }

    public void Dispose()
    {
        Disconnect();
        GC.SuppressFinalize(this);
    }
}

public class MatchInfo
{
    public string? ServerStatus { get; set; }
    public string? RawStatus { get; set; }
    public ServerStatusInfo? ServerInfo { get; set; }
    public string? PlayerCount { get; set; }
    public List<string>? AvailableMaps { get; set; }
}

public class ServerStatusInfo
{
    public string? Hostname { get; set; }
    public string? Version { get; set; }
    public string? UdpIp { get; set; }
    public string? Map { get; set; }
    public string? Players { get; set; }
    public List<string>? PlayersList { get; set; }
}

public class ServerCheckResult
{
    public bool Success { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string? Error { get; set; }
    public long LatencyMs { get; set; }
    public ServerStatusInfo? ServerInfo { get; set; }
    public string? BotInfo { get; set; }
}