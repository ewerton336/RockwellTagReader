using System.Buffers.Binary;

namespace RockwellTagReader;

/// <summary>
/// Result of a single sub-request inside a Multiple Service Packet reply.
/// Either GeneralStatus == 0 with TypeCode + ValueBytes populated, or
/// GeneralStatus != 0 with ErrorMessage populated.
/// </summary>
internal sealed record MspSubResult(
    byte GeneralStatus,
    ushort? TypeCode,
    byte[]? ValueBytes,
    string? ErrorMessage)
{
    public bool IsSuccess => GeneralStatus == 0 && ValueBytes is not null;
}

/// <summary>
/// Parses the CIP reply (the bytes returned in the SendRRData DataItem) for a
/// Multiple Service Packet request and yields one MspSubResult per sub-request,
/// in the same order.
/// </summary>
internal static class MultiServiceResponse
{
    private const byte ExpectedReplyService = 0x8A;             // 0x0A | 0x80
    private const byte StatusEmbeddedServiceError = 0x1E;       // partial — sub-replies still present

    public static IReadOnlyList<MspSubResult> Parse(byte[] cipResponse)
    {
        if (cipResponse.Length < 4)
            throw new InvalidOperationException("MSP response too short");

        var replyService  = cipResponse[0];
        var generalStatus = cipResponse[2];
        var extendedWords = cipResponse[3];

        if (replyService != ExpectedReplyService)
            throw new InvalidOperationException(
                $"MSP reply service mismatch: expected 0x{ExpectedReplyService:X2}, got 0x{replyService:X2}");

        if (generalStatus != 0 && generalStatus != StatusEmbeddedServiceError)
        {
            ushort? extended = extendedWords > 0 && cipResponse.Length >= 4 + 2
                ? BinaryPrimitives.ReadUInt16LittleEndian(cipResponse.AsSpan(4, 2))
                : null;
            var ext = extended is null ? "" : $", extended=0x{extended.Value:X4}";
            throw new InvalidOperationException($"MSP global error: status=0x{generalStatus:X2}{ext}");
        }

        // Service count + offset table live AFTER any extended status words.
        var dataStart = 4 + extendedWords * 2;
        if (cipResponse.Length < dataStart + 2)
            throw new InvalidOperationException("MSP response missing service count");

        var serviceCount = BinaryPrimitives.ReadUInt16LittleEndian(cipResponse.AsSpan(dataStart, 2));
        var offsetTableStart = dataStart + 2;
        if (cipResponse.Length < offsetTableStart + serviceCount * 2)
            throw new InvalidOperationException("MSP response truncated offset table");

        var results = new MspSubResult[serviceCount];
        for (var i = 0; i < serviceCount; i++)
        {
            var subOffset = BinaryPrimitives.ReadUInt16LittleEndian(
                cipResponse.AsSpan(offsetTableStart + i * 2, 2));
            // Offsets are byte offsets from the start of ServiceCount (= dataStart).
            var subStart = dataStart + subOffset;

            int subEnd;
            if (i + 1 < serviceCount)
            {
                var nextOffset = BinaryPrimitives.ReadUInt16LittleEndian(
                    cipResponse.AsSpan(offsetTableStart + (i + 1) * 2, 2));
                subEnd = dataStart + nextOffset;
            }
            else
            {
                subEnd = cipResponse.Length;
            }

            if (subStart < 0 || subEnd > cipResponse.Length || subEnd < subStart + 4)
            {
                results[i] = new MspSubResult(
                    0xFF, null, null, $"sub-reply {i}: invalid bounds (start={subStart}, end={subEnd})");
                continue;
            }

            results[i] = ParseSubReply(cipResponse.AsSpan(subStart, subEnd - subStart));
        }

        return results;
    }

    private static MspSubResult ParseSubReply(ReadOnlySpan<byte> sub)
    {
        if (sub.Length < 4)
            return new MspSubResult(0xFF, null, null, "sub-reply truncated");

        var status   = sub[2];
        var extWords = sub[3];

        if (status != 0)
        {
            ushort? extended = extWords > 0 && sub.Length >= 4 + 2
                ? BinaryPrimitives.ReadUInt16LittleEndian(sub.Slice(4, 2))
                : null;
            var ext = extended is null ? "" : $", extended=0x{extended.Value:X4}";
            return new MspSubResult(status, null, null, $"CIP error: general=0x{status:X2}{ext}");
        }

        var dataStart = 4 + extWords * 2;
        if (sub.Length <= dataStart)
            return new MspSubResult(status, null, null, "sub-reply has no data");

        try
        {
            var (typeCode, valueBytes) = CipReadTag.ExtractReadTagValue(sub[dataStart..]);
            return new MspSubResult(status, typeCode, valueBytes, null);
        }
        catch (Exception ex)
        {
            return new MspSubResult(status, null, null, $"decode error: {ex.Message}");
        }
    }
}
