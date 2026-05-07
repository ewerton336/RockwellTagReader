using System.Buffers.Binary;
using System.Globalization;

namespace RockwellTagReader;

/// <summary>
/// Builds an unconnected CIP "Read Tag" request (service 0x4C) wrapped in
/// "Unconnected Send" (service 0x52) addressed at the Connection Manager
/// (class 0x06, instance 1) of an Allen-Bradley ControlLogix CPU.
/// </summary>
internal static class CipReadTag
{
    private const byte SvcUnconnectedSend = 0x52;
    private const byte SvcReadTag         = 0x4C;
    private const byte ReplyBit           = 0x80;

    /// <summary>
    /// Builds just the embedded Read Tag service bytes
    /// (no Unconnected Send wrapper, no MSP framing).
    /// Layout: [Service 0x4C][PathSize words][SymbolicPath...][ElementCount UINT16]
    /// </summary>
    public static byte[] BuildEmbeddedReadTag(LogixTagPlan plan, ushort elementCount = 1)
    {
        var pathWordSize = LogixTagPath.PathWordSize(plan.CipPath);

        var embedded = new byte[2 + plan.CipPath.Length + 2];
        embedded[0] = SvcReadTag;
        embedded[1] = pathWordSize;
        Buffer.BlockCopy(plan.CipPath, 0, embedded, 2, plan.CipPath.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(embedded.AsSpan(embedded.Length - 2, 2), elementCount);
        return embedded;
    }

    /// <summary>
    /// String-based overload: plans the tag (handling bit-access syntax) and
    /// builds the embedded Read Tag bytes. The CIP wire only contains the parent
    /// path — bit-extraction happens client-side via <see cref="DecodeBit"/>.
    /// </summary>
    public static byte[] BuildEmbeddedReadTag(string tagName, ushort elementCount = 1)
        => BuildEmbeddedReadTag(LogixTagPath.Plan(tagName), elementCount);

    /// <summary>
    /// Wraps any embedded CIP message in an Unconnected Send addressed at the
    /// Connection Manager (class 0x06, instance 1), with the given route path.
    /// </summary>
    public static byte[] WrapInUnconnectedSend(byte[] embeddedMessage, byte[] routePath)
    {
        if (routePath.Length % 2 != 0)
            throw new ArgumentException("Route path must be word-aligned (even length)", nameof(routePath));

        var embeddedPad = embeddedMessage.Length % 2 == 0 ? 0 : 1;
        var routeSizeWords = (byte)(routePath.Length / 2);

        var msg = new byte[
              2                       // service + path size
            + 4                       // path to Connection Manager
            + 2                       // priority time tick + timeout
            + 2                       // message request size
            + embeddedMessage.Length
            + embeddedPad
            + 2                       // route size + reserved
            + routePath.Length];

        var i = 0;
        msg[i++] = SvcUnconnectedSend;
        msg[i++] = 0x02;              // path size: 2 words = 4 bytes
        msg[i++] = 0x20; msg[i++] = 0x06;     // class 0x06
        msg[i++] = 0x24; msg[i++] = 0x01;     // instance 1
        msg[i++] = 0x07;              // priority/time tick
        msg[i++] = 0xE9;              // timeout ticks (~10s)
        BinaryPrimitives.WriteUInt16LittleEndian(msg.AsSpan(i, 2), (ushort)embeddedMessage.Length);
        i += 2;
        Buffer.BlockCopy(embeddedMessage, 0, msg, i, embeddedMessage.Length);
        i += embeddedMessage.Length;
        if (embeddedPad == 1)
            msg[i++] = 0x00;

        msg[i++] = routeSizeWords;
        msg[i++] = 0x00;              // reserved
        Buffer.BlockCopy(routePath, 0, msg, i, routePath.Length);

        return msg;
    }

    /// <summary>
    /// Encodes the Unconnected Send + Read Tag request bytes (single-tag read).
    /// </summary>
    public static byte[] EncodeRequest(string tagName, byte[] routePath, ushort elementCount = 1)
    {
        var embedded = BuildEmbeddedReadTag(tagName, elementCount);
        return WrapInUnconnectedSend(embedded, routePath);
    }

    /// <summary>
    /// Bit-access decoder for <c>tag.N</c> syntax: reads the underlying atomic
    /// (BOOL/SINT/INT/DINT/LINT and unsigned/word variants), masks bit N, and
    /// returns "0" or "1".
    /// </summary>
    public static string DecodeBit(ushort typeCode, byte[] valueBytes, int bitNumber)
    {
        if (bitNumber < 0 || bitNumber >= 64)
            throw new ArgumentOutOfRangeException(nameof(bitNumber), bitNumber, "bit number out of range (0-63)");

        ulong word = typeCode switch
        {
            CipTypeCodes.Bool or CipTypeCodes.Sint or CipTypeCodes.Usint or CipTypeCodes.Byte
                => valueBytes[0],
            CipTypeCodes.Int or CipTypeCodes.Uint or CipTypeCodes.Word
                => BinaryPrimitives.ReadUInt16LittleEndian(valueBytes),
            CipTypeCodes.Dint or CipTypeCodes.Udint or CipTypeCodes.Dword
                => BinaryPrimitives.ReadUInt32LittleEndian(valueBytes),
            CipTypeCodes.Lint or CipTypeCodes.Ulint or CipTypeCodes.Lword
                => BinaryPrimitives.ReadUInt64LittleEndian(valueBytes),
            _ => throw new NotSupportedException($"Bit access not supported for CIP type 0x{typeCode:X4}")
        };

        return ((word >> bitNumber) & 1UL) == 0 ? "0" : "1";
    }

    /// <summary>
    /// Parses a CIP reply header from a span (4 + 2*extWords bytes), validates the reply bit
    /// and the general status, and returns the offset where the service-specific data begins.
    /// </summary>
    public static int ParseCipReplyHeader(ReadOnlySpan<byte> reply)
    {
        if (reply.Length < 4)
            throw new InvalidOperationException("CIP response too short");

        var replyService  = reply[0];
        var generalStatus = reply[2];
        var extendedWords = reply[3];

        if ((replyService & ReplyBit) == 0)
            throw new InvalidOperationException($"CIP reply bit not set (got service 0x{replyService:X2})");

        if (generalStatus != 0)
        {
            ushort? extended = extendedWords > 0 && reply.Length >= 4 + 2
                ? BinaryPrimitives.ReadUInt16LittleEndian(reply.Slice(4, 2))
                : null;
            var ext = extended is null ? "" : $", extended=0x{extended.Value:X4}";
            throw new InvalidOperationException(
                $"CIP error: general=0x{generalStatus:X2}{ext}");
        }

        return 4 + extendedWords * 2;
    }

    /// <summary>
    /// Parses a Read Tag CIP reply: validates the reply, then returns
    /// (typeCode, valueBytes) from the service-specific data.
    /// </summary>
    public static (ushort TypeCode, byte[] ValueBytes) ParseResponse(byte[] cipResponse)
    {
        var dataStart = ParseCipReplyHeader(cipResponse);
        return ExtractReadTagValue(cipResponse.AsSpan(dataStart));
    }

    /// <summary>
    /// Extracts (typeCode, valueBytes) from the data portion of a Read Tag reply
    /// (the bytes after the CIP header + extended status).
    /// </summary>
    public static (ushort TypeCode, byte[] ValueBytes) ExtractReadTagValue(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
            throw new InvalidOperationException("CIP response missing tag data type");

        var typeCode = BinaryPrimitives.ReadUInt16LittleEndian(data[..2]);
        var value = data[2..].ToArray();
        return (typeCode, value);
    }

    /// <summary>
    /// Decodes a CIP atomic value as its string representation.
    /// </summary>
    public static string DecodeValue(ushort typeCode, byte[] valueBytes)
    {
        return typeCode switch
        {
            CipTypeCodes.Bool  => (valueBytes[0] != 0).ToString(),
            CipTypeCodes.Sint  => ((sbyte)valueBytes[0]).ToString(CultureInfo.InvariantCulture),
            CipTypeCodes.Usint or CipTypeCodes.Byte
                               => valueBytes[0].ToString(CultureInfo.InvariantCulture),
            CipTypeCodes.Int   => BinaryPrimitives.ReadInt16LittleEndian(valueBytes).ToString(CultureInfo.InvariantCulture),
            CipTypeCodes.Uint or CipTypeCodes.Word
                               => BinaryPrimitives.ReadUInt16LittleEndian(valueBytes).ToString(CultureInfo.InvariantCulture),
            CipTypeCodes.Dint  => BinaryPrimitives.ReadInt32LittleEndian(valueBytes).ToString(CultureInfo.InvariantCulture),
            CipTypeCodes.Udint or CipTypeCodes.Dword
                               => BinaryPrimitives.ReadUInt32LittleEndian(valueBytes).ToString(CultureInfo.InvariantCulture),
            CipTypeCodes.Lint  => BinaryPrimitives.ReadInt64LittleEndian(valueBytes).ToString(CultureInfo.InvariantCulture),
            CipTypeCodes.Ulint or CipTypeCodes.Lword
                               => BinaryPrimitives.ReadUInt64LittleEndian(valueBytes).ToString(CultureInfo.InvariantCulture),
            CipTypeCodes.Real  => BinaryPrimitives.ReadSingleLittleEndian(valueBytes).ToString(CultureInfo.InvariantCulture),
            CipTypeCodes.Lreal => BinaryPrimitives.ReadDoubleLittleEndian(valueBytes).ToString(CultureInfo.InvariantCulture),
            _ => throw new NotSupportedException($"CIP type 0x{typeCode:X4} not supported")
        };
    }
}
