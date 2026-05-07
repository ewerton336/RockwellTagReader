namespace RockwellTagReader;

/// <summary>
/// Raw outcome of a single tag read inside a Multiple Service Packet response.
/// On success, <see cref="ValueBytes"/> contains the little-endian CIP-encoded
/// payload and <see cref="TypeCode"/> identifies the atomic type — the caller
/// is responsible for decoding. On failure, <see cref="ErrorMessage"/> describes
/// the cause and <see cref="GeneralStatus"/> carries the CIP status byte.
/// </summary>
/// <remarks>
/// Returned by <see cref="RockwellPlcClient.ReadRawAsync"/>. For most callers,
/// <see cref="TagReadResult"/> from <see cref="RockwellPlcClient.ReadManyAsync"/>
/// is the more convenient shape — it carries an already-decoded string value.
/// </remarks>
public sealed record MspRawResult(
    string TagName,
    byte GeneralStatus,
    ushort? TypeCode,
    byte[]? ValueBytes,
    string? ErrorMessage)
{
    /// <summary>True when the read returned data with status 0.</summary>
    public bool IsSuccess => GeneralStatus == 0 && ValueBytes is not null;
}
