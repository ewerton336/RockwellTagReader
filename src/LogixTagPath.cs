using System.Buffers.Binary;
using System.Text;

namespace RockwellTagReader;

/// <summary>
/// Resolved Logix tag reference: the wire-format CIP path bytes plus an optional
/// bit number for "tag.N" syntax (atomic bit access).
/// When BitNumber != null, callers must mask bit N from the read result client-side.
/// </summary>
internal sealed record LogixTagPlan(string OriginalName, byte[] CipPath, int? BitNumber);

/// <summary>
/// Encodes a Logix tag reference as a CIP request path. Supports:
///  - simple symbolic tags: "SPEED_PIMS"   → ANSI Extended Symbolic Segment
///  - struct/UDT members:   "Motor.Speed"  → two concatenated symbolic segments
///  - array elements:       "Arr[5]"       → symbolic + Element Logical Segment
///  - bit access on atomic: "Flag.2"       → CIP wire = parent only; bit masked client-side
///  - combinations:         "Arr[5].State.7"
///
/// Trailing ".N" (digit-only suffix) is treated as bit access, not as an array
/// element. Use "[N]" for explicit array indexing.
/// </summary>
internal static class LogixTagPath
{
    private const byte SymbolicAnsiSegment = 0x91;
    private const byte ElementSegment8     = 0x28;
    private const byte ElementSegment16    = 0x29;
    private const byte ElementSegment32    = 0x2A;

    /// <summary>
    /// Plans a tag read: detects an optional trailing ".N" bit-access suffix,
    /// strips it from the CIP path, and returns both the wire path and the bit number.
    /// </summary>
    public static LogixTagPlan Plan(string tagName)
    {
        if (string.IsNullOrEmpty(tagName))
            throw new ArgumentException("Tag name cannot be empty", nameof(tagName));

        int? bitNumber = null;
        var pathPart = tagName;

        var lastDot = tagName.LastIndexOf('.');
        if (lastDot > 0 && lastDot < tagName.Length - 1)
        {
            var suffix = tagName[(lastDot + 1)..];
            if (suffix.Length > 0
                && suffix.All(char.IsDigit)
                && uint.TryParse(suffix, out var bn)
                && bn < 64)
            {
                bitNumber = (int)bn;
                pathPart = tagName[..lastDot];
            }
        }

        return new LogixTagPlan(tagName, Encode(pathPart), bitNumber);
    }

    public static byte[] Encode(string tagName)
    {
        if (string.IsNullOrEmpty(tagName))
            throw new ArgumentException("Tag name cannot be empty", nameof(tagName));

        var segments = new List<byte[]>();
        var totalLen = 0;
        var i = 0;

        while (i < tagName.Length)
        {
            var ch = tagName[i];

            if (ch == '.')
            {
                i++;
                continue;
            }

            if (ch == '[')
            {
                var end = tagName.IndexOf(']', i + 1);
                if (end < 0)
                    throw new ArgumentException($"Unclosed '[' in tag name: {tagName}", nameof(tagName));
                var numStr = tagName.Substring(i + 1, end - i - 1).Trim();
                if (!uint.TryParse(numStr, out var arrIdx))
                    throw new ArgumentException($"Invalid array index in tag name: {tagName}", nameof(tagName));
                var seg = EncodeElementSegment(arrIdx);
                segments.Add(seg);
                totalLen += seg.Length;
                i = end + 1;
                continue;
            }

            // Read token until next '.' or '['
            var start = i;
            while (i < tagName.Length && tagName[i] != '.' && tagName[i] != '[')
                i++;
            var token = tagName[start..i];

            byte[] tokenSeg = uint.TryParse(token, out var idx)
                ? EncodeElementSegment(idx)
                : EncodeSymbolicSegment(token);
            segments.Add(tokenSeg);
            totalLen += tokenSeg.Length;
        }

        if (segments.Count == 0)
            throw new ArgumentException($"Empty tag path: {tagName}", nameof(tagName));

        var path = new byte[totalLen];
        var pos = 0;
        foreach (var seg in segments)
        {
            Buffer.BlockCopy(seg, 0, path, pos, seg.Length);
            pos += seg.Length;
        }
        return path;
    }

    private static byte[] EncodeSymbolicSegment(string name)
    {
        var nameBytes = Encoding.ASCII.GetBytes(name);
        if (nameBytes.Length == 0 || nameBytes.Length > 255)
            throw new ArgumentException($"Symbolic name length out of range: '{name}'");

        var pad = nameBytes.Length % 2 == 0 ? 0 : 1;
        var seg = new byte[2 + nameBytes.Length + pad];
        seg[0] = SymbolicAnsiSegment;
        seg[1] = (byte)nameBytes.Length;
        Buffer.BlockCopy(nameBytes, 0, seg, 2, nameBytes.Length);
        return seg;
    }

    private static byte[] EncodeElementSegment(uint index)
    {
        if (index <= 0xFF)
            return [ElementSegment8, (byte)index];

        if (index <= 0xFFFF)
        {
            var seg = new byte[4];
            seg[0] = ElementSegment16;
            seg[1] = 0x00;
            BinaryPrimitives.WriteUInt16LittleEndian(seg.AsSpan(2, 2), (ushort)index);
            return seg;
        }

        var seg32 = new byte[6];
        seg32[0] = ElementSegment32;
        seg32[1] = 0x00;
        BinaryPrimitives.WriteUInt32LittleEndian(seg32.AsSpan(2, 4), index);
        return seg32;
    }

    /// <summary>Path size in 16-bit words (the value that goes in the CIP PathSize byte).</summary>
    public static byte PathWordSize(byte[] encodedPath)
    {
        if (encodedPath.Length % 2 != 0)
            throw new InvalidOperationException("Encoded path must be word-aligned");
        return (byte)(encodedPath.Length / 2);
    }

    /// <summary>
    /// Parses a comma-separated route path like "1,0" or "1,0,2,5" into raw CIP route bytes.
    /// Each value becomes one byte. Pads to even length.
    /// </summary>
    public static byte[] ParseRoutePath(string slotPath)
    {
        if (string.IsNullOrWhiteSpace(slotPath))
            return [];

        var parts = slotPath.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var bytes = new byte[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!byte.TryParse(parts[i].Trim(), out var b))
                throw new ArgumentException($"Invalid route path segment '{parts[i]}'", nameof(slotPath));
            bytes[i] = b;
        }

        if (bytes.Length % 2 == 0)
            return bytes;

        var padded = new byte[bytes.Length + 1];
        Buffer.BlockCopy(bytes, 0, padded, 0, bytes.Length);
        return padded;
    }
}
