using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace OpenRevo.Core;

public enum ReadRequest { Capabilities, FanStatus, EnableTelemetry }

public static class ReadProtocol
{
    public const int Port = 13688;
    public const int Slot = 19;
    public const string ClientId = "PluginClient_19";
    // Exact subscriptions only. No generic publish API is exposed.
    public static IReadOnlyList<string> Topics { get; } = Array.AsReadOnly(new[]
    {
        "Customize/SupportInfo", "Fan/Status", "Tray/Status", "System/FanInfo",
        "System/CpuInfo", "System/GpuInfo", "System/BatteryInfo"
    });

    public static (string Topic, string Action) Describe(ReadRequest request) => request switch
    {
        ReadRequest.Capabilities => ("Customize/SupportControl", "GETSUPPORT"),
        ReadRequest.FanStatus => ("Fan/Control", "GETSTATUS"),
        ReadRequest.EnableTelemetry => ("System/Control", "System_ON"),
        _ => throw new ArgumentOutOfRangeException(nameof(request))
    };
}

internal sealed record Packet(byte Header, byte[] Body);

internal static class MqttWire
{
    internal const int MaximumPacketBytes = 1024 * 1024;
    internal static readonly UTF8Encoding Utf8 = new(false, true);

    internal static byte[] String(string value)
    {
        var bytes = Utf8.GetBytes(value);
        if (bytes.Length > ushort.MaxValue || value.Contains('\0')) throw new InvalidDataException("Invalid MQTT string.");
        var result = new byte[bytes.Length + 2];
        BinaryPrimitives.WriteUInt16BigEndian(result, (ushort)bytes.Length);
        bytes.CopyTo(result, 2);
        return result;
    }

    internal static byte[] Frame(byte header, byte[] body)
    {
        if (body.Length > MaximumPacketBytes) throw new InvalidDataException("MQTT packet too large.");
        using var stream = new MemoryStream();
        stream.WriteByte(header);
        var remaining = body.Length;
        do
        {
            var digit = remaining % 128;
            remaining /= 128;
            stream.WriteByte((byte)(digit | (remaining > 0 ? 128 : 0)));
        } while (remaining > 0);
        stream.Write(body);
        return stream.ToArray();
    }

    internal static byte[] Connect(string password)
    {
        using var body = new MemoryStream();
        body.Write(String("MQTT"));
        body.Write(new byte[] { 4, 0xc2, 0, 30 }); // v3.1.1, username/password, clean=true, no will
        body.Write(String(ReadProtocol.ClientId));
        body.Write(String("PluginClient_User_19"));
        body.Write(String(password));
        return Frame(0x10, body.ToArray());
    }

    internal static byte[] Subscribe()
    {
        using var body = new MemoryStream();
        body.Write(new byte[] { 0, 1 });
        foreach (var topic in ReadProtocol.Topics) { body.Write(String(topic)); body.WriteByte(0); }
        return Frame(0x82, body.ToArray());
    }

    internal static byte[] Query(ReadRequest request)
    {
        var (topic, action) = ReadProtocol.Describe(request);
        var json = Utf8.GetBytes(JsonSerializer.Serialize(new { Action = action }));
        return Frame(0x30, [.. String(topic), .. json]); // QoS=0, retain=false
    }

    internal static byte[] Mode(PilotMode mode)
    {
        var action = ModePilotPolicy.Action(mode);
        return Frame(0x30, [.. String("Fan/Control"), .. Utf8.GetBytes(JsonSerializer.Serialize(new { Action = action }))]);
    }

    internal static async Task<Packet> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var one = new byte[1];
        await stream.ReadExactlyAsync(one, cancellationToken);
        var header = one[0];
        var size = 0;
        var multiplier = 1;
        for (var i = 0; i < 4; i++)
        {
            await stream.ReadExactlyAsync(one, cancellationToken);
            var digit = one[0];
            size += (digit & 127) * multiplier;
            if (size > MaximumPacketBytes) throw new InvalidDataException("MQTT packet exceeds 1 MiB.");
            if ((digit & 128) == 0)
            {
                if (i > 0 && digit == 0) throw new InvalidDataException("Non-canonical MQTT length.");
                var body = new byte[size];
                await stream.ReadExactlyAsync(body, cancellationToken);
                return new Packet(header, body);
            }
            multiplier *= 128;
        }
        throw new InvalidDataException("Malformed MQTT remaining length.");
    }

    internal static TopicSample ParsePublish(Packet packet, DateTimeOffset now)
    {
        if ((packet.Header >> 4) != 3 || (packet.Header & 6) != 0 || (packet.Header & 8) != 0)
            throw new InvalidDataException("Unexpected MQTT publish flags/QoS.");
        if (packet.Body.Length < 2) throw new InvalidDataException("Missing MQTT topic.");
        var length = BinaryPrimitives.ReadUInt16BigEndian(packet.Body);
        if (length == 0 || 2 + length > packet.Body.Length) throw new InvalidDataException("Invalid MQTT topic length.");
        var topic = Utf8.GetString(packet.Body, 2, length);
        if (!ReadProtocol.Topics.Contains(topic, StringComparer.Ordinal)) throw new InvalidDataException("Topic outside exact allowlist.");
        using var document = JsonDocument.Parse(Utf8.GetString(packet.Body, 2 + length, packet.Body.Length - 2 - length),
            new JsonDocumentOptions { MaxDepth = 32 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected object status payload.");
        return new TopicSample(topic, document.RootElement.Clone(), now, (packet.Header & 1) != 0);
    }
}
