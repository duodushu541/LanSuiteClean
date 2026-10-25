# LanSuiteClean

局域网远程管理工具 - 纯合法功能，C# .NET 8 独立项目

## 项目简介

LanSuiteClean 是一个基于 C# .NET 8 的局域网远程管理工具，采用全新独立架构设计。仅保留合法的企业级远程管理功能，不包含任何敏感操作（密码窃取、键盘记录、反杀毒、横向移动等）。

### 技术栈

- **语言**: C# 12, .NET 8
- **架构**: 3组件设计 (Client / Server / Admin)
- **通信协议**: 自定义 TCP 帧协议 `[Type:1][Length:4 BE][Payload]`
- **加密**: AES-256-CBC
- **发现机制**: UDP 广播 + TCP 直连
- **GUI**: Windows Forms

## 组件说明

| 组件 | 可执行文件 | 说明 |
|------|-----------|------|
| Client | `LanClient.exe` | 被控端，静默运行，监听命令 |
| Server | `LanServer.exe` | 命令行控制端，设备发现与命令分发 |
| Admin | `LanAdmin.exe` | GUI 图形管理界面，多标签面板 |

## 编译构建

```bash
# 构建 Release 版本
dotnet build LanSuiteClean.sln -c Release

# 输出路径
src/client/bin/x64/Release/net8.0-windows/LanClient.exe
src/server/bin/x64/Release/net8.0-windows/LanServer.exe
src/admin/bin/x64/Release/net8.0-windows/LanAdmin.exe
```

## 使用方式

### 1. 启动服务端

```bash
# 在控制端机器上运行
LanServer.exe
```

### 2. 启动客户端

```bash
# 在被控机器上运行（同一局域网）
LanClient.exe
```

客户端自动通过 UDP 广播（端口 50001）发现服务器，建立 TCP 连接（端口 50000）。

### 3. 启动 GUI 管理界面

```bash
LanAdmin.exe
```

## 支持的合法功能

### 系统与网络
- `get_system_info` - 系统信息（OS、CPU、内存、主机名）
- `get_tcp_connections` - TCP 连接状态
- `get_drives` - 磁盘驱动器信息
- `network_scan` - 局域网 ping 扫描

### 进程管理
- `get_processes` - 获取进程列表
- `kill_process <pid>` - 终止进程

### 窗口管理
- `get_windows` - 获取前台窗口列表
- `minimize_windows` - 最小化所有窗口
- `terminate_windows` - 终止指定窗口

### 文件操作
- `get_dir <path>` - 浏览目录
- `download <path>` - 下载文件
- `delete <path>` - 删除文件
- `rename <old> <new>` - 重命名文件

### 注册表
- `reg_query <hkey> <path>` - 查询注册表值
- `reg_list <hkey> <path>` - 列出子键
- `reg_search <hkey> <path> <keyword>` - 搜索注册表
- `reg_create_key <hkey> <path>` - 创建注册表键
- `reg_delete_key <hkey> <path>` - 删除注册表键
- `reg_read_value <hkey> <path> <name>` - 读取注册表值
- `reg_persistence` - 添加开机自启（注册表 Run 项）

### 辅助功能
- `screenshots` - 屏幕截图
- `clipboard_get` / `clipboard_set` - 剪贴板读写
- `tts_speak "<text>"` - 文字转语音播报
- `get_time` / `set_time` - 时间获取与设置
- `get_proxy` / `set_proxy` - 代理设置
- `lock_screen` - 锁定屏幕
- `visit_website <url>` - 打开网页
- `get_geo` - 地理位置
- `get_user_activity` - 用户活动（最近进程/文件/网络）
- `get_startup` - 启动项列表
- `add_startup` / `remove_startup` - 开机自启管理
- `get_audit_log` / `clear_audit_log` - 审计日志

## 通信协议

### 帧格式
```
[Type: 1 byte][Length: 4 bytes BE][Payload: Length bytes]
```

### 帧类型
| Type | 名称 | 说明 |
|------|------|------|
| 0x31 | Cmd | 命令请求 |
| 0x32 | Data | 数据响应 |
| 0x33 | Ack | 确认 |
| 0x34 | Chunk | 分片数据 |

### 端口
| 端口 | 协议 | 用途 |
|------|------|------|
| 50000 | TCP | 控制通道 |
| 50001 | UDP | 设备发现广播 |

## 安全说明

本项目仅包含合法的企业管理功能：
- 进程管理、窗口管理、文件操作
- 注册表查询、屏幕截图、剪贴板读取
- TTS 语音、代理设置、锁屏
- 网络扫描、地理位置、审计日志

**已排除的功能**：
- 密码窃取（浏览器、凭证转储）
- 键盘记录
- 反杀毒/绕过检测
- 内核驱动操作
- 横向移动（PsExec、SMB）
- 勒索软件
- UAC/EDR 绕过
- Shellcode 加载/反射注入

## 项目结构

```
LanSuiteClean/
├── LanSuiteClean.sln
├── .gitignore
└── src/
    ├── shared/
    │   ├── LanSuite.Shared.csproj
    │   ├── Protocol.cs      # 消息帧协议
    │   └── Crypto.cs        # AES-256-CBC 加密
    ├── client/
    │   ├── LanClient.csproj
    │   └── Program.cs       # 被控客户端
    ├── server/
    │   ├── LanServer.csproj
    │   └── Program.cs       # 命令行服务端
    └── admin/
        ├── LanAdmin.csproj
        └── Program.cs       # WinForms GUI 管理界面
```

## License

MIT
