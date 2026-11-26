using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;

namespace Launcher.Core;

public static class ServerPing
{
    public static async Task<long> PingAsync(string host, int port, CancellationToken token)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(host, port, token);
        await using var stream = client.GetStream();
        using var handshake = new MemoryStream();
        WriteVarInt(handshake, 0);
        WriteVarInt(handshake, 767);
        var name = Encoding.UTF8.GetBytes(host);
        WriteVarInt(handshake, name.Length);
        handshake.Write(name);
        var portBytes = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(portBytes, checked((ushort)port));
        handshake.Write(portBytes);
        WriteVarInt(handshake, 1);
        await WritePacketAsync(stream, handshake.ToArray(), token);
        await WritePacketAsync(stream, [0], token);
        var response = await ReadPacketAsync(stream, token);
        if (response[0] != 0) throw new InvalidDataException("Unexpected Minecraft status packet.");

        var packet = new byte[9];
        packet[0] = 1;
        BinaryPrimitives.WriteInt64BigEndian(packet.AsSpan(1), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var stopwatch = Stopwatch.StartNew();
        await WritePacketAsync(stream, packet, token);
        var reply = await ReadPacketAsync(stream, token);
        if (!packet.AsSpan().SequenceEqual(reply)) throw new InvalidDataException("Unexpected Minecraft ping reply.");
        return stopwatch.ElapsedMilliseconds;
    }

    private static async Task<byte[]> ReadPacketAsync(Stream stream, CancellationToken token)
    {
        var size = 0;
        var next = new byte[1];
        for (var shift = 0; shift < 28; shift += 7)
        {
            await stream.ReadExactlyAsync(next, token);
            size |= (next[0] & 127) << shift;
            if ((next[0] & 128) != 0) continue;
            if (size is < 1 or > 1024 * 1024) throw new InvalidDataException("Invalid Minecraft packet size.");
            var payload = new byte[size];
            await stream.ReadExactlyAsync(payload, token);
            return payload;
        }
        throw new InvalidDataException("Invalid Minecraft packet length.");
    }

    private static async Task WritePacketAsync(Stream stream, byte[] payload, CancellationToken token)
    {
        using var packet = new MemoryStream();
        WriteVarInt(packet, payload.Length);
        packet.Write(payload);
        await stream.WriteAsync(packet.ToArray(), token);
    }

    private static void WriteVarInt(Stream stream, int value)
    {
        while ((value & ~127) != 0)
        {
            stream.WriteByte((byte)((value & 127) | 128));
            value >>= 7;
        }
        stream.WriteByte((byte)value);
    }
}
