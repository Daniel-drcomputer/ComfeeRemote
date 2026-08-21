using System.IO;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using ComfeeRemote.Models;

namespace ComfeeRemote.Services;

/// <summary>
/// Kleine native C#-Implementierung für das lokale Midea/Comfee V2-Protokoll.
/// Keine Python-, pip- oder Fremdbibliothek wird benötigt.
/// </summary>
public sealed class MideaV2Protocol
{
    private const int OuterHeaderLength = 40;
    private const int OuterSignatureLength = 16;

    // Midea LocalSecurity AES key:
    private static readonly byte[] AesKey =
        Convert.FromHexString("6A92EF406BAD2F0359BAAD994171EA6D");

    // Midea LocalSecurity MD5 salt:
    private static readonly byte[] Salt =
        Convert.FromHexString("78686469776A6E6368656B6434643531326368646A783564386534633339344432443753");

    private readonly AppConfig _config;
    private byte _messageSerial;

    public int MessageProtocol { get; private set; }

    public MideaV2Protocol(AppConfig config)
    {
        _config = config;
        MessageProtocol = config.MessageProtocol;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        // Wie midea-local: QueryAppliance mit MessageProtocol 0 senden.
        // Die Antwort enthält die tatsächliche Message-Protocol-Version im Header.
        try
        {
            var query = BuildQueryAppliance();
            var inner = await SendAndReceiveInnerAsync(query, cancellationToken);

            if (inner.Length >= 10 && inner[9] == 0xA0)
            {
                MessageProtocol = inner[8];
                _config.MessageProtocol = MessageProtocol;
                ConfigService.Save(_config);
            }
        }
        catch
        {
            // Manche ältere Geräte beantworten QueryAppliance nicht zuverlässig.
            // Dann funktioniert Protocol 0 bei vielen V2-ACs weiterhin.
            MessageProtocol = _config.MessageProtocol;
        }
    }

    public async Task<AcState> ReadStatusAsync(CancellationToken cancellationToken = default)
    {
        var query = BuildAcQuery(MessageProtocol);
        var inner = await SendAndReceiveInnerAsync(query, cancellationToken);
        return ParseAcStatus(inner);
    }

    public async Task SetStateAsync(AcState state, CancellationToken cancellationToken = default)
    {
        var command = BuildGeneralSet(MessageProtocol, state);
        // Antwort wird gelesen, damit kein TCP-Response im Socket liegen bleibt.
        _ = await SendAndReceiveInnerAsync(command, cancellationToken);
    }

    public async Task ToggleLedAsync(CancellationToken cancellationToken = default)
    {
        var command = BuildToggleDisplay(MessageProtocol);
        _ = await SendAndReceiveInnerAsync(command, cancellationToken);
    }

    private byte NextSerial()
    {
        _messageSerial++;
        if (_messageSerial == 0 || _messageSerial >= 254)
            _messageSerial = 1;
        return _messageSerial;
    }

    private static byte[] BuildQueryAppliance()
    {
        var body = new byte[19];
        return BuildInnerMessage(0xAC, 0, 0xA0, body);
    }

    private byte[] BuildAcQuery(int protocolVersion)
    {
        // MessageQuery: body type 0x41 + 19 bytes + serial + CRC8
        var payload = new byte[19];
        payload[0] = 0x81;
        payload[2] = 0xFF;

        var body = BuildAcBody(0x41, payload);
        return BuildInnerMessage(0xAC, protocolVersion, 0x03, body);
    }

    private byte[] BuildToggleDisplay(int protocolVersion)
    {
        var payload = new byte[]
        {
            0x02,0x00,0xFF,0x02,0x00,0x02,0x00,0x00,0x00,0x00,
            0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00
        };

        var body = BuildAcBody(0x41, payload);
        return BuildInnerMessage(0xAC, protocolVersion, 0x03, body);
    }

    private byte[] BuildGeneralSet(int protocolVersion, AcState s)
    {
        var targetWhole = (int)s.TargetTemperature;
        var half = Math.Abs(s.TargetTemperature * 2 - Math.Round(s.TargetTemperature * 2)) < 0.001
                   && ((int)Math.Round(s.TargetTemperature * 2) % 2 != 0);

        byte b1 = (byte)((s.Power ? 0x01 : 0x00) | 0x40); // prompt tone
        byte b2 = (byte)(((s.Mode << 5) & 0xE0)
                         | (targetWhole & 0x0F)
                         | (half ? 0x10 : 0x00));
        byte b3 = (byte)(s.FanSpeed & 0x7F);
        byte swing = (byte)(0x30
                            | (s.SwingVertical ? 0x0C : 0)
                            | (s.SwingHorizontal ? 0x03 : 0));

        byte b8 = (byte)((s.Turbo ? 0x20 : 0) | (s.PowerSaving ? 0x08 : 0));
        byte b9 = (byte)((s.SmartEye ? 0x01 : 0)
                         | (s.Dry ? 0x04 : 0)
                         | (s.AuxHeating ? 0x08 : 0)
                         | (s.Eco ? 0x80 : 0)
                         | (s.Anion ? 0x20 : 0));
        byte b10 = (byte)((s.Fahrenheit ? 0x04 : 0)
                          | (s.Sleep ? 0x01 : 0)
                          | (s.Turbo ? 0x02 : 0));

        var payload = new byte[]
        {
            b1,
            b2,
            b3,
            0x00,
            0x00,
            0x00,
            swing,
            b8,
            b9,
            b10,
            0x00,
            0x00,
            0x00,
            0x00,
            0x00,
            0x00,
            (byte)(s.NaturalWind ? 0x40 : 0),
            0x00,
            0x00,
            0x00,
            (byte)(s.FrostProtect ? 0x80 : 0),
            (byte)(s.Comfort ? 0x01 : 0)
        };

        var body = BuildAcBody(0x40, payload);
        return BuildInnerMessage(0xAC, protocolVersion, 0x02, body);
    }

    private byte[] BuildAcBody(byte bodyType, byte[] payload)
    {
        // MessageACBase: BodyType + Payload + MessageId + CRC8
        var body = new byte[1 + payload.Length + 2];
        body[0] = bodyType;
        Buffer.BlockCopy(payload, 0, body, 1, payload.Length);
        body[^2] = NextSerial();
        body[^1] = Crc8.Calculate(body.AsSpan(0, body.Length - 1));
        return body;
    }

    private static byte[] BuildInnerMessage(byte deviceType, int protocolVersion, byte messageType, byte[] body)
    {
        const int headerLength = 10;
        var withoutChecksum = new byte[headerLength + body.Length];

        withoutChecksum[0] = 0xAA;
        withoutChecksum[1] = (byte)withoutChecksum.Length;
        withoutChecksum[2] = deviceType;
        withoutChecksum[3] = 0x00;
        withoutChecksum[4] = 0x00;
        withoutChecksum[5] = 0x00;
        withoutChecksum[6] = 0x00;
        withoutChecksum[7] = 0x00;
        withoutChecksum[8] = (byte)protocolVersion;
        withoutChecksum[9] = messageType;
        Buffer.BlockCopy(body, 0, withoutChecksum, headerLength, body.Length);

        byte checksum = MessageChecksum(withoutChecksum.AsSpan(1));

        var result = new byte[withoutChecksum.Length + 1];
        Buffer.BlockCopy(withoutChecksum, 0, result, 0, withoutChecksum.Length);
        result[^1] = checksum;
        return result;
    }

    private static byte MessageChecksum(ReadOnlySpan<byte> data)
    {
        int sum = 0;
        foreach (var b in data)
            sum += b;
        return (byte)((~sum + 1) & 0xFF);
    }

    private async Task<byte[]> SendAndReceiveInnerAsync(
        byte[] innerMessage,
        CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

        await client.ConnectAsync(_config.IpAddress, _config.Port, timeoutCts.Token);

        using NetworkStream stream = client.GetStream();
        stream.ReadTimeout = 5000;
        stream.WriteTimeout = 5000;

        var packet = BuildOuterV2Packet(_config.DeviceId, innerMessage);
        await stream.WriteAsync(packet, timeoutCts.Token);
        await stream.FlushAsync(timeoutCts.Token);

        var outer = await ReadOuterPacketAsync(stream, timeoutCts.Token);
        return DecryptOuterPacket(outer);
    }

    private static byte[] BuildOuterV2Packet(long deviceId, byte[] innerMessage)
    {
        var packet = new List<byte>(OuterHeaderLength + 64);

        packet.AddRange(new byte[]
        {
            0x5A,0x5A,0x01,0x11,0x00,0x00,0x20,0x00,
            0x00,0x00,0x00,0x00,
            0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
            0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
            0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00
        });

        var time = PacketTime();
        for (int i = 0; i < 8; i++)
            packet[12 + i] = time[i];

        Span<byte> idBytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(idBytes, deviceId);
        for (int i = 0; i < 8; i++)
            packet[20 + i] = idBytes[i];

        var encrypted = AesEncrypt(innerMessage);
        packet.AddRange(encrypted);

        int finalLength = packet.Count + OuterSignatureLength;
        packet[4] = (byte)(finalLength & 0xFF);
        packet[5] = (byte)((finalLength >> 8) & 0xFF);

        var signature = Encode32(packet.ToArray());
        packet.AddRange(signature);

        return packet.ToArray();
    }

    private static byte[] PacketTime()
    {
        // Entspricht dem Midea-Format: UTC YYYYMMDDHHMMSSff paarweise, rückwärts.
        string t = DateTime.UtcNow.ToString("yyyyMMddHHmmssff");
        var bytes = new List<byte>(8);
        for (int i = 0; i < 16; i += 2)
            bytes.Insert(0, byte.Parse(t.Substring(i, 2)));
        return bytes.ToArray();
    }

    private static byte[] AesEncrypt(byte[] raw)
    {
        using var aes = Aes.Create();
        aes.Key = AesKey;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;
        using var encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(raw, 0, raw.Length);
    }

    private static byte[] AesDecrypt(byte[] encrypted)
    {
        using var aes = Aes.Create();
        aes.Key = AesKey;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;
        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(encrypted, 0, encrypted.Length);
    }

    private static byte[] Encode32(byte[] raw)
    {
        var combined = new byte[raw.Length + Salt.Length];
        Buffer.BlockCopy(raw, 0, combined, 0, raw.Length);
        Buffer.BlockCopy(Salt, 0, combined, raw.Length, Salt.Length);
        return MD5.HashData(combined);
    }

    private static async Task<byte[]> ReadOuterPacketAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        var first = new byte[6];
        await ReadExactlyAsync(stream, first, cancellationToken);

        if (first[0] != 0x5A || first[1] != 0x5A)
            throw new InvalidDataException("Antwort ist kein Midea-V2-Paket.");

        int length = first[4] | (first[5] << 8);
        if (length < 56 || length > 65535)
            throw new InvalidDataException($"Ungültige Paketlänge: {length}");

        var packet = new byte[length];
        Buffer.BlockCopy(first, 0, packet, 0, first.Length);

        await ReadExactlyAsync(
            stream,
            packet.AsMemory(first.Length, length - first.Length),
            cancellationToken);

        return packet;
    }

    private static async Task ReadExactlyAsync(
        NetworkStream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        int readTotal = 0;
        while (readTotal < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[readTotal..], cancellationToken);
            if (read <= 0)
                throw new IOException("Verbindung wurde vom Klimagerät beendet.");
            readTotal += read;
        }
    }

    private static byte[] DecryptOuterPacket(byte[] outer)
    {
        if (outer.Length <= OuterHeaderLength + OuterSignatureLength)
            throw new InvalidDataException("Midea-Paket ist zu kurz.");

        int cryptLength = outer.Length - OuterHeaderLength - OuterSignatureLength;
        if (cryptLength <= 0 || cryptLength % 16 != 0)
            throw new InvalidDataException("Verschlüsselter Payload hat ungültige Länge.");

        var encrypted = new byte[cryptLength];
        Buffer.BlockCopy(outer, OuterHeaderLength, encrypted, 0, cryptLength);
        return AesDecrypt(encrypted);
    }

    private static AcState ParseAcStatus(byte[] message)
    {
        if (message.Length < 12 || message[0] != 0xAA)
            throw new InvalidDataException("Ungültige AC-Antwort.");

        int bodyLength = message.Length - 10 - 1;
        if (bodyLength <= 0)
            throw new InvalidDataException("AC-Antwort enthält keinen Body.");

        var body = message.AsSpan(10, bodyLength);

        // Das getestete Gerät antwortet auf MessageQuery mit C0.
        if (body[0] != 0xC0 && body[0] != 0xA0)
            throw new InvalidDataException($"Nicht unterstützter AC-Body 0x{body[0]:X2}.");

        if (body[0] == 0xA0)
            return ParseA0(body);

        return ParseC0(body);
    }

    private static AcState ParseC0(ReadOnlySpan<byte> b)
    {
        if (b.Length < 16)
            throw new InvalidDataException("C0-Status ist zu kurz.");

        byte decimalByte = b.Length > 20 ? b[15] : (byte)0;

        return new AcState
        {
            Power = (b[1] & 0x01) != 0,
            Mode = (b[2] & 0xE0) >> 5,
            TargetTemperature = (b[2] & 0x0F) + 16.0 + (((b[2] & 0x10) != 0) ? 0.5 : 0.0),
            FanSpeed = b[3] & 0x7F,
            SwingVertical = (b[7] & 0x0C) != 0,
            SwingHorizontal = (b[7] & 0x03) != 0,
            Turbo = ((b[8] & 0x20) != 0) || ((b[10] & 0x02) != 0),
            PowerSaving = (b[8] & 0x08) != 0,
            SmartEye = (b[8] & 0x40) != 0,
            NaturalWind = (b[9] & 0x02) != 0,
            Dry = (b[9] & 0x04) != 0,
            Eco = (b[9] & 0x10) != 0,
            AuxHeating = (b[9] & 0x08) != 0,
            Anion = (b[9] & 0x20) != 0,
            Fahrenheit = (b[10] & 0x04) != 0,
            Sleep = (b[10] & 0x01) != 0,
            IndoorTemperature = ParseTemperature(b[11], decimalByte & 0x0F),
            OutdoorTemperature = ParseTemperature(b[12], decimalByte >> 4),
            ScreenDisplay = (((b[14] >> 4) & 0x07) != 0x07) && ((b[1] & 0x01) != 0),
            FrostProtect = b.Length >= 22 && (b[21] & 0x80) != 0,
            Comfort = b.Length >= 23 && (b[22] & 0x01) != 0
        };
    }

    private static AcState ParseA0(ReadOnlySpan<byte> b)
    {
        if (b.Length < 15)
            throw new InvalidDataException("A0-Status ist zu kurz.");

        return new AcState
        {
            Power = (b[1] & 0x01) != 0,
            TargetTemperature =
                ((b[1] & 0x3E) >> 1) - 4 + 16.0 + (((b[1] & 0x40) != 0) ? 0.5 : 0.0),
            Mode = (b[2] & 0xE0) >> 5,
            FanSpeed = b[3] & 0x7F,
            SwingVertical = (b[7] & 0x0C) != 0,
            SwingHorizontal = (b[7] & 0x03) != 0,
            Turbo = ((b[8] & 0x20) != 0) || ((b[10] & 0x02) != 0),
            PowerSaving = (b[8] & 0x08) != 0,
            SmartEye = (b[9] & 0x01) != 0,
            Dry = (b[9] & 0x04) != 0,
            AuxHeating = (b[9] & 0x08) != 0,
            Anion = (b[9] & 0x20) != 0,
            Sleep = (b[10] & 0x01) != 0,
            ScreenDisplay = (((b[14] >> 4) & 0x07) != 0x07) && ((b[1] & 0x01) != 0)
        };
    }

    private static double? ParseTemperature(byte integer, int decimalPart)
    {
        if (integer == 0xFF)
            return null;

        double tempInteger = (integer - 50) / 2.0;

        if (decimalPart == 0)
            return tempInteger;

        if (tempInteger < 0)
            return (int)tempInteger - decimalPart * 0.1;

        return (int)tempInteger + decimalPart * 0.1;
    }
}
