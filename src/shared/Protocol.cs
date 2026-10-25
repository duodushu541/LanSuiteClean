using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LanSuite.Shared;

/// <summary>
/// 帧类型前缀
/// </summary>
public static class FrameTypes
{
    public const byte Cmd  = 0x31;
    public const byte Data = 0x32;
    public const byte Ack  = 0x33;
    public const byte Chunk = 0x34;
}

/// <summary>
/// 消息包：[Type:1][Length:4 BE][Payload:Length]
/// </summary>
public class MessagePack
{
    public byte Type;
    public byte[] Payload;
    public string Text => Encoding.UTF8.GetString(Payload);

    public MessagePack(byte type, byte[] payload) { Type = type; Payload = payload; }
    public MessagePack(byte type, string text) : this(type, Encoding.UTF8.GetBytes(text)) { }

    public byte[] ToBytes()
    {
        var len = Payload.Length;
        var header = new byte[5];
        header[0] = Type;
        header[1] = (byte)((len >> 24) & 0xFF);
        header[2] = (byte)((len >> 16) & 0xFF);
        header[3] = (byte)((len >> 8) & 0xFF);
        header[4] = (byte)(len & 0xFF);
        var result = new byte[5 + len];
        Buffer.BlockCopy(header, 0, result, 0, 5);
        Buffer.BlockCopy(Payload, 0, result, 5, len);
        return result;
    }

    public static MessagePack? FromBytes(ReadOnlySpan<byte> data)
    {
        if (data.Length < 5) return null;
        var len = (data[1] << 24) | (data[2] << 16) | (data[3] << 8) | data[4];
        if (data.Length < 5 + len) return null;
        var payload = new byte[len];
        Buffer.BlockCopy(data.ToArray(), 5, payload, 0, len);
        return new MessagePack(data[0], payload);
    }
}

/// <summary>
/// 基于 TCP 的可靠消息通道（自动分包/粘包处理）
/// </summary>
public class MessageChannel
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly byte[] _readBuf = new byte[8192];
    private readonly List<byte> _pending = new();
    private readonly object _lock = new();

    public bool IsConnected => _client.Connected && _client.Client.Connected;
    public string? LocalAddr => _client.Client.LocalEndPoint?.ToString();
    public string? RemoteAddr => _client.Client.RemoteEndPoint?.ToString();

    public MessageChannel(TcpClient client)
    {
        _client = client;
        _stream = client.GetStream();
        _ = ReceiveLoopAsync();
    }

    public void Send(MessagePack msg)
    {
        lock (_lock) { _stream.Write(msg.ToBytes(), 0, msg.ToBytes().Length); _stream.Flush(); }
    }

    public void Send(string text) => Send(new MessagePack(FrameTypes.Data, text));
    public void SendCmd(byte type, string text) => Send(new MessagePack(type, text));

    public void Close()
    {
        try { _stream.Close(); } catch { }
        try { _client.Close(); } catch { }
    }

    private async Task ReceiveLoopAsync()
    {
        try
        {
            while (_client.Connected)
            {
                var n = await _stream.ReadAsync(_readBuf, 0, _readBuf.Length);
                if (n == 0) break;
                lock (_lock) _pending.AddRange(_readBuf.AsSpan(0, n).ToArray());
                while (true)
                {
                    MessagePack? msg;
                    lock (_lock) msg = MessagePack.FromBytes(_pending.ToArray());
                    if (msg == null) break;
                    lock (_lock) _pending.RemoveRange(0, 5 + msg!.Payload.Length);
                    OnMessage?.Invoke(msg);
                }
            }
        }
        catch { }
        finally { OnDisconnected?.Invoke(); }
    }

    public event Action<MessagePack>? OnMessage;
    public event Action? OnDisconnected;
}
