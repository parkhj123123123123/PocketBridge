using System.Net.WebSockets;
using System.Text.Json;

namespace PocketBridge.Core;

/// <summary>Trusted-relay framing used by Apple's Shortcuts, which cannot perform PocketBridge AES-GCM framing.</summary>
public static class ShortcutWire
{
    public static byte[] Encode(byte type, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > Wire.MaxMessageSize - 1) throw new InvalidDataException("메시지가 너무 큽니다.");
        byte[] result = new byte[payload.Length + 1];
        result[0] = type;
        payload.CopyTo(result.AsSpan(1));
        return result;
    }

    public static (byte Type, byte[] Payload) Decode(ReadOnlySpan<byte> packet)
    {
        if (packet.Length is < 1 or > Wire.MaxMessageSize) throw new InvalidDataException("단축어 전송 메시지가 올바르지 않습니다.");
        return (packet[0], packet[1..].ToArray());
    }

    public static async Task SendJsonAsync<T>(WebSocket socket, byte type, T value, CancellationToken cancellationToken)
    {
        byte[] packet = Encode(type, JsonSerializer.SerializeToUtf8Bytes(value, Wire.Json));
        await socket.SendAsync(packet, WebSocketMessageType.Binary, true, cancellationToken).ConfigureAwait(false);
    }

    public static Uri SocketUri(Uri server, string room)
    {
        var builder = new UriBuilder(server)
        {
            Scheme = server.Scheme == "https" ? "wss" : "ws",
            Path = $"/ws/shortcut/{Uri.EscapeDataString(room)}/receiver"
        };
        return builder.Uri;
    }
}
