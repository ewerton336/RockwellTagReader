using System.Buffers.Binary;

namespace RockwellTagReader;

/// <summary>
/// Builds CIP "Multiple Service Packet" (service 0x0A) bodies that pack N
/// embedded sub-requests addressed at the Message Router (class 0x02, instance 1).
/// </summary>
internal static class MultiServiceRequest
{
    private const byte SvcMultipleService = 0x0A;

    // Path to Message Router: class 0x02, instance 1 → 0x20 0x02 0x24 0x01 (4 bytes / 2 words).
    private static readonly byte[] MessageRouterPath = [0x20, 0x02, 0x24, 0x01];

    // MSP body overhead = service+pathsize (2) + Message Router path (4) + service count (2).
    private const int MspHeaderBytes = 2 + 4 + 2;
    private const int OffsetEntryBytes = 2;

    /// <summary>
    /// Encodes the MSP body (service 0x0A targeted at the Message Router) wrapping the
    /// provided embedded sub-requests in order. Offsets are relative to the start of
    /// the ServiceCount field (immediately after the request path).
    /// </summary>
    public static byte[] EncodeMspBody(IReadOnlyList<byte[]> subRequests)
    {
        if (subRequests.Count == 0)
            throw new ArgumentException("MSP requires at least one sub-request", nameof(subRequests));

        var n = subRequests.Count;
        var subTotal = 0;
        for (var i = 0; i < n; i++) subTotal += subRequests[i].Length;

        var bodyLen = MspHeaderBytes + (n * OffsetEntryBytes) + subTotal;
        var body = new byte[bodyLen];

        var pos = 0;
        body[pos++] = SvcMultipleService;
        body[pos++] = 0x02;                               // path size in words = 2 (4 bytes)
        Buffer.BlockCopy(MessageRouterPath, 0, body, pos, 4);
        pos += 4;

        // Service count
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(pos, 2), (ushort)n);
        pos += 2;

        // Offset table — first sub-request starts right after the offset table,
        // measured from the start of the ServiceCount field (= 2 + 2*n bytes in).
        var offsetTableStart = pos;
        var subStart = 2 + (n * OffsetEntryBytes);
        for (var k = 0; k < n; k++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(
                body.AsSpan(offsetTableStart + k * OffsetEntryBytes, 2), (ushort)subStart);
            subStart += subRequests[k].Length;
        }
        pos += n * OffsetEntryBytes;

        // Sub-requests, concatenated in order.
        for (var k = 0; k < n; k++)
        {
            Buffer.BlockCopy(subRequests[k], 0, body, pos, subRequests[k].Length);
            pos += subRequests[k].Length;
        }

        return body;
    }

    /// <summary>
    /// Encodes a full MSP request ready to be sent through the EIP session:
    /// builds N embedded Read Tag sub-requests, packs them in an MSP body,
    /// and wraps the body in an Unconnected Send.
    /// </summary>
    public static byte[] EncodeRequest(IReadOnlyList<string> tagNames, byte[] routePath, ushort elementCount = 1)
    {
        var subRequests = new byte[tagNames.Count][];
        for (var i = 0; i < tagNames.Count; i++)
            subRequests[i] = CipReadTag.BuildEmbeddedReadTag(tagNames[i], elementCount);

        var mspBody = EncodeMspBody(subRequests);
        return CipReadTag.WrapInUnconnectedSend(mspBody, routePath);
    }

    /// <summary>
    /// Splits tag names into batches such that each batch's MSP body fits inside
    /// <paramref name="byteBudget"/> bytes (default 470 — safe under the typical
    /// 504-byte unconnected-send limit on ControlLogix).
    /// </summary>
    public static List<List<string>> SplitTagsIntoBatches(IReadOnlyList<string> tagNames, int byteBudget = 470)
    {
        var batches = new List<List<string>>();
        var current = new List<string>();
        var currentBytes = MspHeaderBytes;

        foreach (var tag in tagNames)
        {
            var subLen = CipReadTag.BuildEmbeddedReadTag(tag).Length;
            var addCost = OffsetEntryBytes + subLen;

            if (current.Count > 0 && currentBytes + addCost > byteBudget)
            {
                batches.Add(current);
                current = new List<string>();
                currentBytes = MspHeaderBytes;
            }

            current.Add(tag);
            currentBytes += addCost;
        }

        if (current.Count > 0) batches.Add(current);
        return batches;
    }
}
